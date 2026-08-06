// Artplayer-based player bridge — variant B (full custom HUD).
//
// Artplayer's built-in chrome is hidden via CSS (.art-bottom, .art-progress,
// .art-state). We render our own HUD as an Artplayer `layer`, which keeps it
// inside the player DOM tree so it survives browser fullscreen and doesn't
// fight Artplayer's chrome for z-index.
//
// HUD layout matches design_v2/animarr/project/design-system/04-tv.html § T-03
// plus the additions from the iteration on 2026-05-27:
//   • Bottom row buttons share the same height (Play not enlarged).
//   • Audio / Subtitles buttons show only the label, no current selection.
//   • Extra controls: Aspect ratio, Audio sync (offset slider), Cast.
//   • Top-right meta line carries every video tag we can detect.
//   • Two styles via localStorage `animarr_player_style`:
//       - "full"  (default) — glass chip with icon + text
//       - "icons" — just the icon, no background, no label
//   • Arrow keys ±10s. Media-key + TV-remote bindings handled directly so
//     standard remote keys (play/pause, prev/next, FF/REW, back) "just work".

(function () {
    // elementId → entry; see attach() for the shape.
    const WIRED = new Map();

    // ── User audio/subtitle preferences ─────────────────────────────────────
    // Pushed once from MediaDetail via animarrPlayer.setPrefs(...) when the
    // account prefs load. audioLang/subLang are ISO-639-1 codes ('ja','ru') or
    // null; subSize is a pixel size or 0 (= leave Artplayer's default).
    //   • subLang  → picks the initial subtitle track by language (client-side
    //                overlay only — never touches the video/audio pipeline).
    //   • subSize  → Artplayer cue font-size.
    //   • audioLang→ auto-selects the matching audio track ONLY on a transcoding
    //                HLS session (see attach). Direct Play is never disturbed:
    //                we never force a transcode just to switch audio language.
    let PREFS = { audioLang: null, subLang: null, subSize: 0 };
    function setPrefs(p) {
        PREFS = {
            audioLang: (p && p.audioLang) ? String(p.audioLang).toLowerCase() : null,
            subLang:   (p && p.subLang)   ? String(p.subLang).toLowerCase()   : null,
            subSize:   (p && Number.isFinite(p.subSize)) ? Math.max(0, p.subSize) : 0,
        };
    }
    // Normalise any track language tag (ISO-639-2/B or /T, ISO-639-1, or a full
    // English name) to a 639-1 2-letter code so preference matching is tolerant
    // of how a muxer tagged the stream. Scope mirrors LanguageNameMap.
    const LANG_2TO1 = {
        jpn:'ja', ja:'ja', eng:'en', en:'en', rus:'ru', ru:'ru',
        ger:'de', deu:'de', de:'de', fre:'fr', fra:'fr', fr:'fr',
        spa:'es', es:'es', ita:'it', it:'it', por:'pt', pt:'pt',
        chi:'zh', zho:'zh', cmn:'zh', yue:'zh', zh:'zh', cn:'zh',
        kor:'ko', ko:'ko', tha:'th', th:'th', vie:'vi', vi:'vi',
        ind:'id', id:'id', ara:'ar', ar:'ar', hin:'hi', hi:'hi', tur:'tr', tr:'tr',
        japanese:'ja', english:'en', russian:'ru', german:'de', french:'fr',
        spanish:'es', italian:'it', portuguese:'pt', mandarin:'zh', chinese:'zh',
        korean:'ko', thai:'th', vietnamese:'vi', indonesian:'id', arabic:'ar',
        hindi:'hi', turkish:'tr',
    };
    function normLang(l) {
        if (!l) return '';
        const k = String(l).trim().toLowerCase();
        if (k === 'und' || k === '') return '';
        return LANG_2TO1[k] || (k.length === 2 ? k : '');
    }

    // Prefer the MAUI loopback media-proxy base when present
    // (window.animarrLocalProxyBase = http://127.0.0.1:<port>, published by the
    // Android host). All WebView fetches then go to the proxy, which forwards to
    // the real server — a trustworthy localhost origin Chromium won't
    // mixed-content-block, so no base64 bridge and no freeze. In a plain browser
    // the proxy base is absent and we use the real server base directly.
    // Evaluated per-call so it's correct even if the proxy base lands after the
    // first animarrSetApiBase().
    const apiBase = () => (typeof window !== 'undefined' &&
        (window.animarrLocalProxyBase || window.animarrApiBase)) || '';
    const apiUrl  = (path) => apiBase() + path;

    // animarrSetApiBase — passthrough setter. The mixed-content workaround
    // moved from "rewrite the base" to "wrap fetch + XHR" (see the IIFE
    // below) because embedding the target URL inside the proxy PATH got
    // mangled: Chromium normalises the path (collapses `//`, decodes `%2F`)
    // before the request leaves the renderer, turning http://server into
    // http:/server. Query-string params aren't path-normalised, so the
    // wrappers stash the full URL in `?u=<encoded>` instead.
    window.animarrSetApiBase = function (newBase) {
        window.animarrApiBase    = newBase || '';
        window.animarrApiBaseRaw = newBase || '';
    };

    // Mixed-content shim for MAUI BlazorWebView (Android).
    //
    // MAUI mounts the Razor bundle at https://0.0.0.x/ (hardcoded virtual
    // host). When the Animarr server lives at plain http://LAN-ip:port,
    // Chromium's renderer refuses every fetch/XHR from the HTTPS page as
    // "active mixed content" — even with MixedContentMode=ALWAYS_ALLOW.
    //
    // A custom Android WebViewClient can't fix it: MAUI re-installs its OWN
    // WebViewClient after ours (it needs it to serve the bundle from the
    // virtual host), so our ShouldInterceptRequest never runs. The reliable
    // channel is plain JS↔.NET interop. So we monkey-patch window.fetch and
    // XMLHttpRequest to route every http:// request through the
    // HttpProxyBridge.ProxyFetch JSInvokable, which runs the request via
    // native HttpClient (no mixed-content rules) and returns the bytes
    // base64-encoded. hls.js uses XHR, so wrapping both covers the manifest +
    // every segment fetch + our /api/* calls.
    //
    // Only active on an HTTPS page where DotNet interop exists (MAUI). On a
    // plain-HTTP browser host the wrappers are no-ops (needsProxy stays
    // false because the page itself is HTTP, so direct fetch works).
    (function installMixedContentProxy() {
        if (typeof window === 'undefined') return;
        if (typeof window.location === 'undefined' || window.location.protocol !== 'https:') return;
        if (window.__animarrProxyInstalled) return;
        window.__animarrProxyInstalled = true;

        const needsProxy = (u) =>
            typeof u === 'string'
            && u.toLowerCase().startsWith('http://')
            // Loopback IS the local media proxy — a trustworthy origin Chromium
            // serves from the HTTPS page without mixed-content blocking, so let
            // it through natively. Only genuinely-remote http:// (a server URL
            // that somehow wasn't routed through the proxy) falls back to the
            // base64 bridge.
            && !/^http:\/\/(127\.0\.0\.1|localhost|\[::1\])(?::|\/|$)/i.test(u);
        const dotnetReady = () =>
            typeof window.DotNet !== 'undefined'
            && typeof window.DotNet.invokeMethodAsync === 'function';
        const b64ToBytes = (b64) => {
            const bin = atob(b64 || '');
            const len = bin.length;
            const arr = new Uint8Array(len);
            for (let i = 0; i < len; i++) arr[i] = bin.charCodeAt(i);
            return arr;
        };
        const headerObj = (h) => {
            const out = {};
            try {
                if (!h) return out;
                if (typeof h.forEach === 'function') {           // Headers instance
                    h.forEach((v, k) => { out[k] = v; });
                } else if (Array.isArray(h)) {
                    h.forEach(([k, v]) => { out[k] = v; });
                } else if (typeof h === 'object') {
                    for (const k in h) out[k] = h[k];
                }
            } catch {}
            return out;
        };
        const proxyFetch = (url, method, headers, body) =>
            window.DotNet.invokeMethodAsync('Animarr.App', 'ProxyFetch',
                url, method || 'GET', headers || {},
                (typeof body === 'string') ? body : null);

        // ── fetch ──────────────────────────────────────────────────────
        if (typeof window.fetch === 'function') {
            const origFetch = window.fetch.bind(window);
            window.fetch = async function (input, init) {
                const url = (typeof input === 'string') ? input
                          : (input && input.url) || null;
                if (!needsProxy(url) || !dotnetReady()) return origFetch(input, init);
                const method  = (init && init.method) || (input && input.method) || 'GET';
                const headers = headerObj((init && init.headers) || (input && input.headers));
                const body    = (init && init.body) || null;
                const r = await proxyFetch(url, method, headers, body);
                const bytes = b64ToBytes(r.bodyBase64);
                if (!r.status) {
                    throw new TypeError('animarr proxy fetch failed: '
                        + new TextDecoder().decode(bytes));
                }
                // 1xx/204/205/304 are "null body status" — the Response
                // constructor throws if you hand it a body for these. Our
                // keepalive endpoint returns 204, so guard it.
                const nullBody = r.status === 101 || r.status === 204
                              || r.status === 205 || r.status === 304;
                return new Response(nullBody ? null : bytes, {
                    status: r.status,
                    headers: { 'Content-Type': r.contentType || 'application/octet-stream' },
                });
            };
        }

        // ── XMLHttpRequest (hls.js's XhrLoader) ────────────────────────
        // We can't subclass XHR cleanly, so we shadow the instance props
        // (status/readyState/response/...) with Object.defineProperty after
        // the native object never gets opened/sent. hls.js reads these in its
        // readystatechange handler — all the accessors below are what it
        // touches.
        if (typeof window.XMLHttpRequest === 'function') {
            const P = XMLHttpRequest.prototype;
            const origOpen   = P.open;
            const origSend   = P.send;
            const origHeader = P.setRequestHeader;
            const origAbort  = P.abort;

            P.open = function (method, url, ...rest) {
                this.__animarr = (needsProxy(url) && dotnetReady())
                    ? { method: method || 'GET', url, headers: {}, aborted: false }
                    : null;
                // ALWAYS call the native open — even for proxied URLs. open()
                // doesn't touch the network (the mixed-content block happens
                // at send()), but it transitions the XHR to OPENED state.
                // hls.js sets `xhr.responseType = 'arraybuffer'` right after
                // open(), and that setter throws InvalidStateError unless the
                // object is OPENED. send() below skips the native send for
                // proxied requests, so no cleartext fetch ever fires.
                return origOpen.call(this, method, url, ...rest);
            };
            P.setRequestHeader = function (name, value) {
                if (this.__animarr) {
                    this.__animarr.headers[name] = value;
                    // Don't forward to native — we're not sending the native
                    // request, and some headers (Range etc.) on the dead
                    // native request are pointless. The proxy call carries
                    // them instead.
                    return;
                }
                return origHeader.call(this, name, value);
            };
            P.abort = function () {
                if (this.__animarr) { this.__animarr.aborted = true; return; }
                return origAbort.call(this);
            };
            P.send = function (body) {
                const ctx = this.__animarr;
                if (!ctx) return origSend.call(this, body);
                const self = this;
                const wantBuffer = self.responseType === 'arraybuffer';
                proxyFetch(ctx.url, ctx.method, ctx.headers, body)
                    .then(function (r) {
                        if (ctx.aborted) return;
                        const bytes = b64ToBytes(r.bodyBase64);
                        const resp  = wantBuffer ? bytes.buffer
                                                 : new TextDecoder().decode(bytes);
                        const def = (k, v) => Object.defineProperty(self, k, { configurable: true, get: () => v });
                        def('readyState', 4);
                        def('status', r.status || 0);
                        def('statusText', r.status ? 'OK' : '');
                        def('response', resp);
                        if (!wantBuffer) def('responseText', resp);
                        def('responseURL', ctx.url);
                        self.getAllResponseHeaders = () => 'content-type: ' + (r.contentType || '') + '\r\n';
                        self.getResponseHeader = (h) =>
                            (String(h).toLowerCase() === 'content-type') ? (r.contentType || null) : null;
                        const prog = { loaded: bytes.length, total: bytes.length, lengthComputable: true };
                        const fire = (kind, evt) => {
                            try { const cb = self['on' + kind]; if (typeof cb === 'function') cb(evt); } catch {}
                            try { self.dispatchEvent(evt); } catch {}
                        };
                        if (!r.status) {
                            fire('error', new ProgressEvent('error'));
                            fire('loadend', new ProgressEvent('loadend'));
                            return;
                        }
                        fire('readystatechange', new Event('readystatechange'));
                        fire('progress', new ProgressEvent('progress', prog));
                        fire('load', new ProgressEvent('load', prog));
                        fire('loadend', new ProgressEvent('loadend', prog));
                    })
                    .catch(function () {
                        if (ctx.aborted) return;
                        Object.defineProperty(self, 'readyState', { configurable: true, get: () => 4 });
                        Object.defineProperty(self, 'status', { configurable: true, get: () => 0 });
                        try { if (typeof self.onerror === 'function') self.onerror(new ProgressEvent('error')); } catch {}
                        try { self.dispatchEvent(new ProgressEvent('error')); } catch {}
                    });
            };
        }

        // eslint-disable-next-line no-console
        console.info('animarr: mixed-content proxy installed (fetch + XHR → ProxyFetch bridge)');
    })();

    // ── formatting helpers ────────────────────────────────────────────
    function formatTime(sec) {
        if (!Number.isFinite(sec) || sec < 0) sec = 0;
        const h = Math.floor(sec / 3600);
        const m = Math.floor((sec % 3600) / 60);
        const s = Math.floor(sec % 60);
        const pad = (n) => String(n).padStart(2, '0');
        return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
    }
    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }
    /** Build the player accent colour from the active --accent-hue (set by the
     *  theme / user accent picker), keeping the player's own bright L/C so it
     *  reads well over video. Mirrors the --hud-accent token in
     *  animarr-player.css. Falls back to hue 245 (blue) when unset. */
    function accentThemeColor() {
        let hue = 245;
        try {
            const v = getComputedStyle(document.documentElement)
                .getPropertyValue('--accent-hue').trim();
            const n = parseFloat(v);
            if (Number.isFinite(n)) hue = n;
        } catch {}
        return `oklch(0.72 0.18 ${hue})`;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Player adapter abstraction (Phase 1, 2026-05-27)
    // ─────────────────────────────────────────────────────────────────
    //
    // The HUD (attachHud, button callbacks, autoCropIfUltrawide, progress
    // reporter) talks to an abstract player surface so Phase 2 can swap in
    // a native ExoPlayer-backed adapter on Android TV without rewriting any
    // UI code. There's no formal abstract base in JS — adapters duck-type
    // the following contract:
    //
    //   Properties (gettable):
    //     playing      : boolean
    //     currentTime  : number (seconds, settable)
    //     duration     : number (seconds)
    //     volume       : number (0..1, settable)
    //     muted        : boolean (settable)
    //     fullscreen   : boolean (settable — toggles browser fullscreen)
    //
    //   Methods:
    //     play()                          → Promise|void
    //     pause()                         → void
    //     on(event, fn)                   → void   (events: play, pause,
    //                                              ended, timeupdate,
    //                                              loadedmetadata, loadeddata,
    //                                              durationchange,
    //                                              volumechange, progress)
    //     off(event, fn)                  → void
    //     once(event, fn)                 → void
    //     setSubtitle({url, name, type})  → void   (url=null disables)
    //     setSubtitleVisible(bool)        → void
    //     setAspectRatio(value)           → void   ('default' | '16:9' | …)
    //     rawVideoElement()               → HTMLVideoElement|null
    //                                              (only for canvas sampling
    //                                               in letterbox detect; null
    //                                               on native adapter)
    //     destroy()                       → void
    //
    // Adapters store any underlying handles (Artplayer instance, hls.js,
    // ExoPlayer bridge) privately; the HUD never touches them.

    /** Wraps an Artplayer 5 instance to expose the abstract surface above.
     *  Maps Artplayer's prefixed event names (`video:timeupdate`) onto the
     *  un-prefixed ones the HUD code subscribes to. */
    class ArtplayerAdapter {
        constructor(art) {
            this.art = art;
            // Set by enableDirectStream() when the source is a progressive
            // /api/video remux (no byte-Range → seek = reload). Null = a
            // normal seekable source (Direct Play / HLS).
            this._ds = null;
        }

        // Bridge map between abstract event names and Artplayer's. Anything
        // not in this map is forwarded as-is (lets Artplayer-specific code,
        // e.g. customType wiring, still subscribe to raw events via art.on).
        static EVENT_MAP = {
            play:           'video:play',
            pause:          'video:pause',
            ended:          'video:ended',
            timeupdate:     'video:timeupdate',
            loadedmetadata: 'video:loadedmetadata',
            loadeddata:     'video:loadeddata',
            durationchange: 'video:durationchange',
            volumechange:   'video:volumechange',
            progress:       'video:progress',
        };

        // ── Properties ────────────────────────────────────────────────
        get playing()      { return !!this.art.playing; }
        get currentTime()  {
            // Direct Stream: while a seek-reload is in flight, report the
            // target so the scrub bar doesn't snap to the old position before
            // the reloaded stream's PTS-shifted timeline catches up. Release
            // only once the video actually lands NEAR the target — by absolute
            // distance, NOT ">=": a backward seek's old position is already
            // past the target, so ">=" cleared it instantly and the reload
            // (which then read a null target) never fired.
            if (this._ds && this._ds.target != null) {
                const ct = this.art.currentTime || 0;
                if (Math.abs(ct - this._ds.target) < 1.5) this._ds.target = null;
                else return this._ds.target;
            }
            return this.art.currentTime || 0;
        }
        set currentTime(t) {
            if (this._ds) { this._directStreamSeek(t); return; }
            try { this.art.currentTime = t; } catch {}
        }
        get duration()     {
            // Progressive remux has no Content-Length → video.duration is
            // often Infinity. Report the server-probed total instead.
            if (this._ds) return this._ds.dur || 0;
            return this.art.duration || 0;
        }
        get volume()       { return this.art.volume ?? 1; }
        set volume(v)      { try { this.art.volume = v; } catch {} }
        get muted()        { return !!this.art.muted; }
        set muted(m)       { try { this.art.muted = m; } catch {} }
        get fullscreen()   { return !!document.fullscreenElement; }
        set fullscreen(b)  { try { this.art.fullscreen = b; } catch {} }

        // ── Playback ──────────────────────────────────────────────────
        play()  { try { return this.art.play();  } catch {} }
        pause() { try { this.art.pause(); } catch {} }

        // ── Direct Stream (progressive remux) ─────────────────────────
        // Turns on seek-by-reload for an /api/video source. baseUrl is the
        // remux URL WITHOUT any ?seek (we append it per seek); totalDuration
        // is the server-probed length the bar/clock read off.
        enableDirectStream(baseUrl, totalDuration) {
            this._ds = { base: baseUrl, dur: totalDuration || 0, target: null };
        }
        // True only when the browser will satisfy a seek to `t` instantly —
        // the position is both already buffered AND inside a reported seekable
        // range. For a no-Range progressive stream Chrome often exposes
        // seekable = the buffered window, so most ±10s skips land here. When it
        // doesn't (seekable empty), we fall back to a remux-reload — no regress.
        _dsCanNativeSeek(v, t) {
            try {
                const s = v.seekable;
                let inSeekable = false;
                for (let i = 0; i < s.length; i++)
                    if (t >= s.start(i) && t <= s.end(i)) { inSeekable = true; break; }
                if (!inSeekable) return false;
                const b = v.buffered;
                for (let j = 0; j < b.length; j++)
                    if (t >= b.start(j) && t <= b.end(j) - 0.5) return true;
            } catch {}
            return false;
        }
        _directStreamSeek(t) {
            const ds = this._ds;
            if (!ds) return;
            const dur = ds.dur || 0;
            t = Math.max(0, dur > 1 ? Math.min(t, dur - 1) : t);
            const v = this.art.video;
            // Fast path: already-buffered + seekable → native instant seek, no
            // remux reload. Cancels any pending reload and clears the hold.
            if (v && this._dsCanNativeSeek(v, t)) {
                if (this._dsTimer) { clearTimeout(this._dsTimer); this._dsTimer = null; }
                ds.target = null;
                try { v.currentTime = t; } catch {}
                return;
            }
            ds.target = t;
            // Debounce the remux-reload: a scrub drag fires currentTime= on
            // every pointermove and each reload spawns a fresh ffmpeg. The
            // getter reports ds.target meanwhile so the bar tracks the drag —
            // we only re-request once the user settles (~280ms).
            if (this._dsTimer) clearTimeout(this._dsTimer);
            this._dsTimer = setTimeout(() => {
                this._dsTimer = null;
                if (!this._ds) return;
                const seekTo = this._ds.target;
                if (seekTo == null) return;
                const v = this.art.video;
                if (!v) return;
                const url = this._ds.base + (this._ds.base.includes('?') ? '&' : '?')
                          + 'seek=' + seekTo.toFixed(3);
                try {
                    const wasPlaying = !v.paused;
                    v.src = url;
                    v.load();
                    const onMeta = () => {
                        v.removeEventListener('loadedmetadata', onMeta);
                        if (wasPlaying) { const p = v.play(); if (p && p.catch) p.catch(() => {}); }
                    };
                    v.addEventListener('loadedmetadata', onMeta);
                } catch (e) { console.warn('direct-stream reload failed', e); }
            }, 280);
        }

        // ── Events ────────────────────────────────────────────────────
        on(event, fn) {
            const e = ArtplayerAdapter.EVENT_MAP[event] || event;
            this.art.on(e, fn);
        }
        off(event, fn) {
            const e = ArtplayerAdapter.EVENT_MAP[event] || event;
            try { this.art.off(e, fn); } catch {}
        }
        once(event, fn) {
            const e = ArtplayerAdapter.EVENT_MAP[event] || event;
            this.art.once(e, fn);
        }

        // ── Subtitle ──────────────────────────────────────────────────
        setSubtitle(opts) {
            if (!opts || !opts.url) {
                try { this.art.subtitle.show = false; } catch {}
                return;
            }
            try {
                this.art.subtitle.switch(opts.url, {
                    type:  opts.type || 'vtt',
                    name:  opts.name || '',
                    escape: false,
                });
                this.art.subtitle.show = true;
            } catch (e) { console.warn('subtitle switch failed', e); }
        }
        setSubtitleVisible(b) {
            try { this.art.subtitle.show = !!b; } catch {}
        }

        // ── Aspect ratio ──────────────────────────────────────────────
        // object-fit:cover is what makes baked-in letterbox bars crop off;
        // 'default' restores Artplayer's contain behaviour.
        setAspectRatio(value) {
            try {
                this.art.aspectRatio = value;
                const video = this.art.video || this.art.template?.$video;
                if (!video) return;
                if (value === 'default') {
                    video.style.objectFit = '';
                    video.style.aspectRatio = '';
                } else {
                    video.style.objectFit = 'cover';
                    video.style.aspectRatio = value.replace(':', ' / ');
                }
            } catch (e) { console.warn('aspectRatio set failed', e); }
        }

        // ── Raw video element (canvas sampling only) ──────────────────
        rawVideoElement() { return this.art.video || null; }

        destroy() {
            try { this.art.destroy(true); } catch {}
        }
    }

    /** Native (ExoPlayer/AVPlayer) adapter. Used on Android TV where
     *  HDR / Dolby Vision passthrough through the WebView is unreliable;
     *  the MAUI host (Animarr.App) draws video into a TextureView behind
     *  the WebView, and the HUD floats on top inside the (now-transparent)
     *  body. Bridge proxy: window.animarrNativePlayer (see MAUI index.html).
     *
     *  No <video> element exists on this path — `rawVideoElement()`
     *  intentionally returns null so consumers (letterbox detection,
     *  picture-in-picture) skip over the native adapter.
     *
     *  Play position + state come from polling the C# bridge every 250ms;
     *  changes diff against the previous tick and fire 'timeupdate' /
     *  'play' / 'pause' / 'ended' / 'durationchange' events to match the
     *  ArtplayerAdapter contract that attachHud() consumes. */
    class NativeAdapter {
        constructor(bridge, opts) {
            this.bridge = bridge;
            this.container = opts.container;
            this._listeners = new Map();
            // Seed duration from the start-session response so the HUD's
            // dur label paints right away (before the first poll lands).
            this._state = {
                positionMs:  Math.round((opts.resumeSec || 0) * 1000),
                durationMs:  Math.round((opts.durationSec || 0) * 1000),
                playing:     false,
                ended:       false,
                buffering:   false,
            };
            this._poll = null;
        }

        // Start the bridge playback then begin polling. Async so attach()
        // can await both calls in sequence.
        async _start(url, resumeSec) {
            try { await this.bridge.play(url, { resumeSec }); }
            catch (e) { console.warn('NativeAdapter: bridge.play failed', e); }
            this._startPoll();
        }

        _startPoll() {
            if (this._poll) return;
            this._poll = setInterval(async () => {
                let s;
                try { s = await this.bridge.getState(); }
                catch { return; }   // single tick failure — try again next time
                if (!s) return;
                const prev = this._state;
                // The C# bridge sends camelCased keys via System.Text.Json's
                // default policy: PositionMs → positionMs, Playing → playing.
                this._state = {
                    positionMs:    s.positionMs    ?? prev.positionMs,
                    durationMs:    s.durationMs    ?? prev.durationMs,
                    playing:       !!s.playing,
                    ended:         !!s.ended,
                    buffering:     !!s.buffering,
                    errorMessage:  s.errorMessage  || null,
                    actualCodec:   s.actualCodec   || '',
                    actualBitDepth:s.actualBitDepth|| 0,
                    actualWidth:   s.actualWidth   || 0,
                    actualHeight:  s.actualHeight  || 0,
                };
                if (this._state.durationMs !== prev.durationMs) this._emit('durationchange');
                if (this._state.positionMs !== prev.positionMs) this._emit('timeupdate');
                if ( this._state.playing && !prev.playing)      this._emit('play');
                if (!this._state.playing &&  prev.playing)      this._emit('pause');
                if ( this._state.ended   && !prev.ended)        this._emit('ended');
                // Fatal error transition — non-null Message means the native
                // player won't recover on its own. We surface it once (diff
                // against prev) so the HUD can toast / log without spam.
                if (this._state.errorMessage && this._state.errorMessage !== prev.errorMessage) {
                    console.error('NativePlayer error:', this._state.errorMessage);
                    this._emit('error');
                }
            }, 250);
        }
        _emit(event) {
            const list = this._listeners.get(event);
            if (!list) return;
            for (const fn of list) {
                try { fn(); } catch (e) { console.warn('native adapter listener threw', e); }
            }
        }

        // ── Properties (PlayerAdapter contract) ──────────────────────
        get playing()       { return this._state.playing; }
        get currentTime()   { return this._state.positionMs / 1000; }
        set currentTime(t)  {
            const ms = Math.max(0, Math.round((t || 0) * 1000));
            this._state.positionMs = ms;
            try { this.bridge.seek(ms); } catch {}
        }
        get duration()      { return this._state.durationMs / 1000; }
        // Volume / mute cached locally because ExoPlayer doesn't push a
        // change event back through our polling path. setter dispatches to
        // bridge → ExoPlayer.Volume = v. Mute is modelled as volume 0 with
        // the prev value remembered so un-mute restores.
        get volume()        { return this._volume ?? 1; }
        set volume(v)       {
            const f = Math.max(0, Math.min(1, +v || 0));
            this._volume = f;
            try { this.bridge.setVolume(f); } catch {}
        }
        get muted()         { return !!this._muted; }
        set muted(m)        {
            const mute = !!m;
            if (mute === this._muted) return;
            if (mute) {
                this._volumeBeforeMute = this.volume;
                this._muted = true;
                try { this.bridge.setVolume(0); } catch {}
            } else {
                this._muted = false;
                const restore = this._volumeBeforeMute ?? 1;
                this._volume = restore;
                try { this.bridge.setVolume(restore); } catch {}
            }
        }
        // Native player is intrinsically fullscreen (TextureView fills
        // the activity), and Android's window IS fullscreen on TV — so
        // the FS button is a no-op. We still expose `false` so the icon
        // doesn't read as "Exit FS" on the HUD.
        get fullscreen()    { return false; }
        set fullscreen(_)   { /* no-op — already fullscreen on TV */ }

        // ── Methods (PlayerAdapter contract) ─────────────────────────
        async play()  { try { await this.bridge.resume(); } catch {} }
        async pause() { try { await this.bridge.pause();  } catch {} }

        on(event, fn) {
            if (!this._listeners.has(event)) this._listeners.set(event, []);
            this._listeners.get(event).push(fn);
        }
        off(event, fn) {
            const list = this._listeners.get(event);
            if (!list) return;
            const i = list.indexOf(fn);
            if (i >= 0) list.splice(i, 1);
        }
        once(event, fn) {
            const wrap = () => { this.off(event, wrap); fn(); };
            this.on(event, wrap);
        }

        // Subtitle (sideloaded WebVTT URL from /api/subtitle). Native player
        // rebuilds the MediaItem on switch — a ~50ms gap, position carries
        // over. Pass {url: null} to disable.
        setSubtitle(opts) {
            try {
                this.bridge.setSubtitle(opts?.url || null, opts?.lang || null);
            } catch (e) { console.warn('native setSubtitle failed', e); }
        }
        setSubtitleVisible(b) {
            // ExoPlayer doesn't have a "hide subtitle" toggle separate from
            // unloading — flipping visible=false means "no subtitle". If the
            // caller has the URL in mind, calling setSubtitle({url:null})
            // is the canonical "off". This is here for contract parity.
            if (!b) { try { this.bridge.setSubtitle(null, null); } catch {} }
        }
        setAspectRatio(value) {
            try { this.bridge.setAspect(value || 'default'); }
            catch (e) { console.warn('native setAspect failed', e); }
        }

        rawVideoElement()    { return null; }

        destroy() {
            if (this._poll) { clearInterval(this._poll); this._poll = null; }
            try { this.bridge.detach(); } catch {}
        }
    }

    /** Availability gate for the native (ExoPlayer/AVPlayer) adapter.
     *  The MAUI host publishes `window.animarrNativePlayer`; on a plain browser
     *  there's no such global so this resolves false (web player is used).
     *
     *  Async on purpose: the authoritative answer comes from a .NET call
     *  (NativePlayerIsAvailable), and the Blazor call dispatcher isn't ready
     *  during early boot — a one-shot boot probe races it and throws "No call
     *  dispatcher has been set", leaving the native gate permanently off. By
     *  awaiting this at attach() time (user just hit play) the dispatcher is
     *  guaranteed ready, so the probe succeeds. Falls back to the cached sync
     *  getter if the async variant isn't present. */
    async function isNativeAdapterAvailable() {
        const np = (typeof window !== 'undefined') ? window.animarrNativePlayer : null;
        if (!np) return false;
        if (typeof np.isAvailableAsync === 'function') {
            try { return !!(await np.isAvailableAsync()); } catch { /* fall through */ }
        }
        return !!(typeof np.isAvailable === 'function' && np.isAvailable());
    }

    /** Picks which audio-offset channel applies. Returns 'hw' / 'sw' / null.
     *  Server-side `output.plan` is authoritative — it tells us exactly which
     *  ffmpeg path was picked. Falls back to a probe-derived guess only if
     *  the start response didn't include an output block (older server). */
    function determineOffsetChannel(output, probeInfo) {
        if (output && output.plan) {
            switch (output.plan) {
                case 'directplay':     return null;     // no transcode
                case 'ts-copy':        return null;     // PCR-sync, no -itsoffset
                case 'vaapi-reencode':
                case 'nvenc-reencode': return 'hw';     // -bf 0 + browser chain latency
                case 'fmp4-copy':      return 'sw';     // B-frames preserved, fMP4 TFDT residual
                default:               return null;
            }
        }
        // Legacy fallback (probe-based heuristic).
        if (!probeInfo) return null;
        if (probeInfo.playbackTier === 'directplay') return null;
        const codec = (probeInfo.videoCodec || '').toLowerCase();
        if (codec === 'h264') return null;
        if (codec === 'hevc') return probeInfo.bitDepth >= 10 ? 'sw' : 'hw';
        return 'hw';
    }

    /** Merge in-file audio streams and discovered external dub files into one
     *  ordered list for the Audio picker. Embedded streams come first (their
     *  source index preserved); external entries follow, each labelled with its
     *  file extension in parentheses (e.g. "Russian · AniLilia (mka)") so the
     *  user can tell a sideload dub from a muxed track. Each option carries a
     *  `current` flag so the popup highlights the active selection.
     *  Shape: { kind:'embedded', index, label, current }
     *       | { kind:'external', path,  label, current } */
    function buildAudioOptions(entry) {
        const opts = [];
        const extActive = entry.currentExternalAudioPath || null;
        (entry.audioList || []).forEach(a => {
            opts.push({
                kind: 'embedded', index: a.index, label: a.label,
                current: !extActive && a.index === entry.currentAudIdx,
            });
        });
        (entry.externalAudioList || []).forEach(t => {
            opts.push({
                kind: 'external', path: t.path,
                label: t.label + ' (' + t.ext + ')',
                current: extActive === t.path,
            });
        });
        return opts;
    }

    /** Read the user's HUD style preference. Defaults to "icons" (the
     *  minimal naked-icons variant) per user spec on 2026-05-27. */
    function readStylePref() {
        try {
            const v = localStorage.getItem('animarr_player_style');
            if (v === 'icons' || v === 'full') return v;
        } catch {}
        return 'icons';
    }

    /** Read the saved playback volume (0..1). Defaults to 1.0. */
    function readVolumePref() {
        try {
            const v = parseFloat(localStorage.getItem('animarr_player_volume'));
            if (Number.isFinite(v) && v >= 0 && v <= 1) return v;
        } catch {}
        return 1.0;
    }
    function saveVolumePref(v) {
        try { localStorage.setItem('animarr_player_volume', String(v)); } catch {}
    }

    // ── SVG icons (feather-style, 18px, stroke-based) ─────────────────
    // Kept as one big map so the icons-only mode renders crisply and both
    // modes look consistent. Each is a 24x24 viewBox so we can re-size
    // uniformly with `width=18`.
    const I = {
        back:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M19 12H5"/><path d="m12 19-7-7 7-7"/></svg>',
        prev:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="19 20 9 12 19 4 19 20"/><line x1="5" y1="19" x2="5" y2="5"/></svg>',
        next:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="5 4 15 12 5 20 5 4"/><line x1="19" y1="5" x2="19" y2="19"/></svg>',
        play:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="0" stroke-linejoin="round"><polygon points="6 4 20 12 6 20 6 4"/></svg>',
        pause:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="0"><rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/></svg>',
        fwd10:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><text x="12" y="15.5" text-anchor="middle" font-size="8" font-weight="800" fill="var(--hud-accent-hi)" stroke="none" font-family="Inter, system-ui, sans-serif">10</text></svg>',
        back10: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/><text x="12" y="15.5" text-anchor="middle" font-size="8" font-weight="800" fill="var(--hud-accent-hi)" stroke="none" font-family="Inter, system-ui, sans-serif">10</text></svg>',
        audio:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></svg>',
        cc:     '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="3"/><path d="M7 11h4"/><path d="M7 14.5h7"/><path d="M16 11h1"/></svg>',
        aspect: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3v12a3 3 0 0 0 3 3h12"/><path d="M18 21V9a3 3 0 0 0-3-3H3"/></svg>',
        offset: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="8"/><path d="M12 13V9.5"/><path d="M9.5 2.5h5"/><path d="M12 2.5v3"/><path d="M18.5 7l1.2-1.2"/></svg>',
        cast:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2 17a3 3 0 0 1 3 3"/><path d="M2 13a7 7 0 0 1 7 7"/><path d="M2 9 a11 11 0 0 1 11 11"/><path d="M2 5h17a2 2 0 0 1 2 2v9"/></svg>',
        volume: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>',
        mute:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><line x1="22" y1="9" x2="16" y2="15"/><line x1="16" y1="9" x2="22" y2="15"/></svg>',
        fsEnter:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9V5a2 2 0 0 1 2-2h4"/><path d="M15 3h4a2 2 0 0 1 2 2v4"/><path d="M21 15v4a2 2 0 0 1-2 2h-4"/><path d="M9 21H5a2 2 0 0 1-2-2v-4"/></svg>',
        fsExit: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3v4a2 2 0 0 1-2 2H3"/><path d="M21 9h-4a2 2 0 0 1-2-2V3"/><path d="M15 21v-4a2 2 0 0 1 2-2h4"/><path d="M3 15h4a2 2 0 0 1 2 2v4"/></svg>',
        info:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9.5"/><line x1="12" y1="11" x2="12" y2="16.5" stroke-width="2.2"/><circle cx="12" cy="7.6" r="1.25" fill="currentColor" stroke="none"/></svg>',
        quality:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.324.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.7 7.7 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.5 6.5 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.5 6.5 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.9 6.9 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z"/><circle cx="12" cy="12" r="3"/></svg>',
        settings:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M10.33 4.32c.42-1.76 2.92-1.76 3.34 0a1.72 1.72 0 0 0 2.58 1.07c1.54-.94 3.3.82 2.37 2.37a1.72 1.72 0 0 0 1.06 2.57c1.76.42 1.76 2.92 0 3.34a1.72 1.72 0 0 0-1.07 2.58c.94 1.54-.82 3.3-2.37 2.37a1.72 1.72 0 0 0-2.57 1.06c-.42 1.76-2.92 1.76-3.34 0a1.72 1.72 0 0 0-2.58-1.07c-1.54.94-3.3-.82-2.37-2.37a1.72 1.72 0 0 0-1.06-2.57c-1.76-.42-1.76-2.92 0-3.34a1.72 1.72 0 0 0 1.07-2.58c-.94-1.54.82-3.3 2.37-2.37a1.72 1.72 0 0 0 2.57-1.06z"/><circle cx="12" cy="12" r="3"/></svg>',
    };

    /** Fullscreen action. On the MAUI Android host this ROTATES the device
     *  (YouTube-style): portrait → landscape to "go fullscreen", landscape →
     *  portrait to exit. The orientation change in turn drives immersive mode
     *  (status-bar hide) via the orientation listener in attachHud. On a plain
     *  browser there's no orientation bridge, so we fall back to the real
     *  Fullscreen API via the adapter. */
    function toggleFullscreen(adapter) {
        if (typeof window !== 'undefined' && typeof window.animarrSetOrientation === 'function') {
            const landscape = !!(window.matchMedia &&
                window.matchMedia('(orientation: landscape)').matches);
            window.animarrSetOrientation(landscape ? 'portrait' : 'landscape');
            return;
        }
        try { adapter.fullscreen = !adapter.fullscreen; } catch {}
    }

    // ── HUD HTML ──────────────────────────────────────────────────────
    function buildHudHtml(style) {
        // Outer .vp-hud is pointer-events:none so the centre passes clicks
        // through to the video; .vp-hud__top / __bottom set pointer-events:auto
        // so buttons remain interactive.
        // `title` attributes give native browser tooltips on hover — required
        // by the icons-only style where the visual label is hidden and the
        // user wouldn't otherwise know what an icon does.
        // The fwd10 button has a sibling `.vp-icon-text` span containing
        // literal "+10"; CSS hides the SVG and shows the text only in icons
        // mode so the action remains readable without a tooltip.
        return `
<div class="vp-hud vp-hud--hidden" data-visible="false" data-style="${escapeHtml(style)}">
  <div class="vp-hud__tap"></div>
  <div class="vp-hud__top">
    <button type="button" class="vp-btn vp-btn--g vp-btn--back" data-act="back"
            aria-label="Back" title="Back (Esc)">
      <span class="vp-glyph">${I.back}</span><span class="vp-label">Back</span>
    </button>
    <div class="vp-hud__title">
      <div class="vp-hud__kicker" data-bind="kicker"></div>
      <div class="vp-hud__name"   data-bind="name"></div>
    </div>
    <div class="vp-hud__meta" data-bind="meta"></div>
  </div>

  <div class="vp-hud__center">
    <button class="vp-hud__cplay" data-act="play" aria-label="Play / pause" title="Play / pause (Space)">
      <span class="vp-glyph" data-bind="play-icon">${I.play}</span>
    </button>
  </div>

  <div class="vp-hud__bottom">
    <div class="vp-hud__progress">
      <span class="vp-hud__time" data-bind="cur">0:00</span>
      <div class="vp-hud__track" data-act="seek">
        <div class="vp-hud__track-buffer" data-bind="buffer"></div>
        <div class="vp-hud__track-fill"   data-bind="fill"></div>
        <div class="vp-hud__thumb"        data-bind="thumb"></div>
        <div class="vp-hud__preview" data-bind="preview">
          <div class="vp-hud__preview-img"  data-bind="preview-img"></div>
          <div class="vp-hud__preview-time" data-bind="preview-time">0:00</div>
        </div>
      </div>
      <span class="vp-hud__time" data-bind="dur">0:00</span>
    </div>
    <div class="vp-hud__controls">
      <div class="vp-hud__cluster vp-hud__cluster--primary">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="back10"
                aria-label="Back 10 seconds" title="Back 10s (←)">
          <span class="vp-glyph">${I.back10}</span>
          <span class="vp-icon-text" aria-hidden="true">-10</span>
          <span class="vp-label">-10s</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="prev"
                aria-label="Previous episode" title="Previous episode (◄◄)">
          <span class="vp-glyph">${I.prev}</span><span class="vp-label">Prev</span>
        </button>
        <button class="vp-btn vp-btn--p vp-btn--tv" data-act="play"
                aria-label="Play / pause" title="Play / pause (Space)">
          <span class="vp-glyph" data-bind="play-icon">${I.play}</span>
          <span class="vp-label" data-bind="play-label">Play</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="next"
                aria-label="Next episode" title="Next episode (►►|)">
          <span class="vp-glyph">${I.next}</span><span class="vp-label">Next</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="fwd10"
                aria-label="Forward 10 seconds" title="Forward 10s (→)">
          <span class="vp-glyph">${I.fwd10}</span>
          <span class="vp-icon-text" aria-hidden="true">+10</span>
          <span class="vp-label">+10s</span>
        </button>
      </div>
      <div class="vp-hud__cluster vp-hud__cluster--secondary">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="volume"
                aria-label="Volume" title="Volume" data-bind="volume-btn">
          <span class="vp-glyph" data-bind="volume-icon">${I.volume}</span>
          <span class="vp-label">Volume</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="quality"
                aria-label="Quality" title="Quality (resolution)">
          <span class="vp-glyph">${I.quality}</span><span class="vp-label">Quality</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="aspect"
                aria-label="Aspect ratio" title="Aspect ratio">
          <span class="vp-glyph">${I.aspect}</span><span class="vp-label">Aspect</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cast"
                aria-label="Cast to TV" title="Send video to TV (DLNA)"
                data-bind="cast-btn">
          <span class="vp-glyph">${I.cast}</span><span class="vp-label">Cast</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="offset"
                aria-label="Audio sync" title="Audio sync offset"
                data-bind="offset-btn">
          <span class="vp-glyph">${I.offset}</span>
          <span class="vp-icon-text" aria-hidden="true">AS</span>
          <span class="vp-label">Sync</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="audio"
                aria-label="Audio tracks" title="Audio tracks">
          <span class="vp-glyph">${I.audio}</span>
          <span class="vp-icon-text" aria-hidden="true">AT</span>
          <span class="vp-label">Audio</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cc"
                aria-label="Subtitles" title="Subtitles">
          <span class="vp-glyph">${I.cc}</span>
          <span class="vp-icon-text" aria-hidden="true">CC</span>
          <span class="vp-label">CC</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="info"
                aria-label="Media info" title="Media info (codec, HDR, audio)">
          <span class="vp-glyph">${I.info}</span>
          <span class="vp-icon-text" aria-hidden="true">i</span>
          <span class="vp-label">Info</span>
        </button>
      </div>
      <button class="vp-btn vp-btn--g vp-btn--tv vp-hud__gear" data-act="settings"
              aria-label="Settings" title="Settings">
        <span class="vp-glyph">${I.settings}</span><span class="vp-label">Settings</span>
      </button>
      <button class="vp-btn vp-btn--g vp-btn--tv vp-hud__fs" data-act="fullscreen"
              aria-label="Fullscreen" title="Fullscreen (F)" data-bind="fs-btn">
        <span class="vp-glyph" data-bind="fs-icon">${I.fsEnter}</span>
        <span class="vp-label" data-bind="fs-label">Fullscreen</span>
      </button>
    </div>
  </div>
</div>`.trim();
    }

    // ── settings menu (mobile gear) ───────────────────────────────────
    // Worded list whose rows each fire a callback. Same popup shell + outside-
    // click / toggle behaviour as the pickers; tapping a row opens that
    // control's own picker (which replaces this menu — both are .vp-hud-popup).
    function openMenuPopup(root, anchor, title, rows) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            // Touch devices fire the opener's click and then a synthesized
            // "ghost" click ~300ms later (double-tap-zoom heuristic). Without
            // this grace window that second click re-enters here and toggles the
            // just-opened menu straight back shut ("appears and immediately
            // disappears"). A deliberate re-tap to close still works afterwards.
            if (wasFor === anchor && Date.now() - Number(existing.dataset.openedAt || 0) < 400) return;
            existing.remove();
            if (wasFor === anchor) return;   // gear tapped again → toggle closed
        }
        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup vp-hud-popup--menu';
        popup.setAttribute('data-anchor', anchor);
        popup.innerHTML = `
            <div class="vp-hud-popup__title">${escapeHtml(title)}</div>
            <div class="vp-hud-popup__list">
                ${rows.map((r, i) => `
                    <button class="vp-hud-popup__item" data-i="${i}">${escapeHtml(r.label)}</button>
                `).join('')}
            </div>`;
        popup.addEventListener('click', (e) => {
            const it = e.target.closest('[data-i]');
            if (!it) return;
            const i = parseInt(it.dataset.i, 10);
            popup.remove();
            const row = rows[i];
            if (row && typeof row.run === 'function') row.run();
        });
        popup.dataset.openedAt = String(Date.now());
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) { document.removeEventListener('click', onDoc, true); return; }
                if (Date.now() - Number(popup.dataset.openedAt || 0) < 400) return;  // ignore the opening tap's ghost click
                if (!popup.contains(e.target) && !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
    }

    // ── popup picker (CC / Audio / Aspect) ────────────────────────────
    function openPickerPopup(root, anchor, title, items, currentIdx, onPick) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            existing.remove();
            if (wasFor === anchor) return;
        }
        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup';
        popup.setAttribute('data-anchor', anchor);
        popup.innerHTML = `
            <div class="vp-hud-popup__title">${escapeHtml(title)}</div>
            <div class="vp-hud-popup__list">
                ${items.map((it, i) => `
                    <button class="vp-hud-popup__item${i === currentIdx ? ' is-active' : ''}"
                            data-i="${i}">${escapeHtml(it)}</button>
                `).join('')}
            </div>`;
        popup.addEventListener('click', (e) => {
            const it = e.target.closest('[data-i]');
            if (!it) return;
            const i = parseInt(it.dataset.i, 10);
            onPick(i);
            popup.remove();
        });
        popup.dataset.openedAt = String(Date.now());
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) {
                    document.removeEventListener('click', onDoc, true);
                    return;
                }
                if (Date.now() - Number(popup.dataset.openedAt || 0) < 400) return;  // ignore the opening tap's ghost click
                if (!popup.contains(e.target) &&
                    !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
    }

    // ── media-info popup (read-only) ──────────────────────────────────
    // Shows the post-transcode video/audio details (resolution, codec, bit
    // depth, HDR, audio, playback path) that used to live in the top-right meta
    // line. Toggled by the bottom "Info" button.
    function openInfoPopup(root, anchor, lines) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            existing.remove();
            if (wasFor === anchor) return;  // same button → toggle closed
        }
        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup vp-hud-popup--info';
        popup.setAttribute('data-anchor', anchor);
        const rows = (lines && lines.length) ? lines : ['No media info available'];
        popup.innerHTML = `
            <div class="vp-hud-popup__title">Media info</div>
            <div class="vp-hud-popup__list">
                ${rows.map(l => `<div class="vp-hud-popup__info">${escapeHtml(l)}</div>`).join('')}
            </div>`;
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) { document.removeEventListener('click', onDoc, true); return; }
                if (!popup.contains(e.target) && !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
    }

    // ── audio-offset slider popup ─────────────────────────────────────
    // `labelOverride` lets the caller relabel the slider (e.g. "External dub")
    // when the 'sw' channel is being reused for external-audio sync rather than
    // its usual HDR/10-bit stream-copy role.
    function openOffsetPopup(root, anchor, channel, labelOverride) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            existing.remove();
            if (wasFor === anchor) return;
        }
        if (!window.animarrCalibrate) return;
        const current = window.animarrCalibrate.getCached(channel)
            ?? (channel === 'hw' ? 70 : 0);
        const min = -200;
        const max = channel === 'sw' ? 700 : 300;
        const label = labelOverride || (channel === 'sw'
            ? 'HDR / 10-bit (stream-copy)'
            : 'Standard (re-encode)');

        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup vp-hud-popup--slider';
        popup.setAttribute('data-anchor', anchor);
        popup.innerHTML = `
            <div class="vp-hud-popup__title">Audio sync · ${escapeHtml(label)}</div>
            <div class="vp-hud-popup__slider-row">
                <input type="range" min="${min}" max="${max}" step="5" value="${current}">
                <span class="vp-hud-popup__slider-value">${current} ms</span>
            </div>
            <div class="vp-hud-popup__hint">Reopen the video to apply (saves immediately)</div>
        `;
        const range = popup.querySelector('input[type=range]');
        const valueEl = popup.querySelector('.vp-hud-popup__slider-value');
        range.addEventListener('input', () => {
            const v = parseInt(range.value, 10);
            valueEl.textContent = v + ' ms';
            window.animarrCalibrate.setManual(channel, v);
        });
        popup.addEventListener('mousedown', e => e.stopPropagation());
        popup.addEventListener('click',     e => e.stopPropagation());
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) {
                    document.removeEventListener('click', onDoc, true);
                    return;
                }
                if (!popup.contains(e.target) &&
                    !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
    }

    // ── volume slider popup ───────────────────────────────────────────
    function openVolumePopup(root, anchor, adapter, onChange) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            existing.remove();
            if (wasFor === anchor) return;
        }
        const cur = Math.round((adapter.volume ?? readVolumePref()) * 100);
        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup vp-hud-popup--slider';
        popup.setAttribute('data-anchor', anchor);
        popup.innerHTML = `
            <div class="vp-hud-popup__title">Volume</div>
            <div class="vp-hud-popup__slider-row">
                <input type="range" min="0" max="100" step="1" value="${cur}">
                <span class="vp-hud-popup__slider-value">${cur}%</span>
            </div>`;
        const range   = popup.querySelector('input[type=range]');
        const valueEl = popup.querySelector('.vp-hud-popup__slider-value');
        range.addEventListener('input', () => {
            const v = parseInt(range.value, 10);
            valueEl.textContent = v + '%';
            const f = v / 100;
            adapter.volume = f;
            adapter.muted  = (f === 0);
            saveVolumePref(f);
            onChange(f);
        });
        popup.addEventListener('mousedown', e => e.stopPropagation());
        popup.addEventListener('click',     e => e.stopPropagation());
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) {
                    document.removeEventListener('click', onDoc, true);
                    return;
                }
                if (!popup.contains(e.target) &&
                    !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
    }

    // ── cast (DLNA) popup ─────────────────────────────────────────────
    // Lists renderers discovered via SSDP on the LAN. Clicking a renderer
    // POSTs /api/dlna/play with the current file + position so playback
    // resumes on the TV side. We pause local playback after a successful
    // hand-off so the user isn't watching the same scene twice.
    async function openCastPopup(root, anchor, mediaPath, adapter) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            existing.remove();
            if (wasFor === anchor) return;
        }
        let renderers = [];
        try {
            const res = await fetch(apiUrl('/api/dlna/renderers'));
            if (res.ok) renderers = await res.json();
        } catch (e) { console.warn('cast: renderer fetch failed', e); }

        const labels = (renderers && renderers.length > 0)
            ? renderers.map(r => r.friendlyName || r.modelName || r.udn)
            : ['(No DLNA devices found on the network)'];
        openPickerPopup(root, anchor, 'Send to TV', labels,
            -1, async (i) => {
                if (!renderers[i]) return;
                try {
                    await fetch(apiUrl('/api/dlna/play'), {
                        method: 'POST',
                        headers: { 'content-type': 'application/json' },
                        body: JSON.stringify({
                            rendererUdn: renderers[i].udn,
                            filePath:    mediaPath,
                            startTimeMs: Math.round((adapter.currentTime || 0) * 1000),
                        }),
                    });
                    adapter.pause();
                } catch (e) { console.warn('cast: play failed', e); }
            });
    }

    // ── auto-detect letterbox + ultrawide → 21:9/2.35 crop ────────────
    // Triggered once on the first decoded frame. Samples a 240-wide canvas
    // of the video, counts black rows at top + bottom, infers the "real"
    // content aspect ratio inside the source frame, and — IF the viewport
    // is at least 21:9 — applies the closest standard aspect (21:9 or
    // 2.35:1) via object-fit:cover so the baked-in bars get cropped off.
    //
    // Skips when:
    //   • viewport is narrower than 21:9 (would crop a normal 16:9 movie
    //     just because the user has a tall window),
    //   • user already manually picked an aspect this session,
    //   • first frame is too dark to meaningfully measure,
    //   • the source frame doesn't have detectable letterbox bars.
    //
    // Caveats:
    //   • The first decoded frame may be a black intro card — in that case
    //     no letterbox is detected and we leave aspect alone (the user can
    //     trigger the Aspect picker manually). Re-sampling several seconds
    //     in would be more robust but adds latency to playback start.
    //   • Requires the <video> to be CORS-clean (we set crossorigin on
    //     moreVideoAttr); getImageData throws SecurityError otherwise and
    //     we silently swallow it.
    function detectLetterbox(adapter) {
        const video = adapter && adapter.rawVideoElement();
        if (!video || !video.videoWidth || !video.videoHeight) return null;
        try {
            const canvas = document.createElement('canvas');
            const w = canvas.width = 240;
            const h = canvas.height = Math.max(8,
                Math.round(video.videoHeight * (w / video.videoWidth)));
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(video, 0, 0, w, h);
            const data = ctx.getImageData(0, 0, w, h).data;

            // Average row brightness on the 0..255 scale (RGB averaged).
            const rowBrightness = (y) => {
                let sum = 0;
                for (let x = 0; x < w; x++) {
                    const i = (y * w + x) * 4;
                    sum += data[i] + data[i + 1] + data[i + 2];
                }
                return sum / (w * 3);
            };
            // Frame-wide brightness — used to detect "this is a fully black
            // intro frame, retry later" cases.
            let totalBrightness = 0;
            for (let y = 0; y < h; y++) totalBrightness += rowBrightness(y);
            totalBrightness /= h;
            if (totalBrightness < 6) return { tooDark: true };

            const THRESHOLD = 12;  // tolerate slight noise above pure black
            let topBlack = 0;
            while (topBlack < h / 3 && rowBrightness(topBlack) < THRESHOLD) topBlack++;
            let botBlack = 0;
            while (botBlack < h / 3 && rowBrightness(h - 1 - botBlack) < THRESHOLD) botBlack++;

            const totalBlack = topBlack + botBlack;
            // Need ≥6% of frame height to be black — anything less is noise
            // or padding from incorrect scaling.
            if (totalBlack < h * 0.06) return { topBlack, botBlack, innerRatio: null };

            const innerH = h - totalBlack;
            const innerRatio = video.videoWidth / (video.videoHeight * innerH / h);
            return { topBlack, botBlack, innerRatio };
        } catch (e) {
            console.warn('letterbox detect: getImageData threw', e);
            return null;
        }
    }

    /** Map a measured inner-content aspect ratio onto our preset aspect
     *  list. Returns the index into the aspect popup's items array, or
     *  null if no preset is a good fit. */
    function bestAspectIdxFor(innerRatio) {
        if (!Number.isFinite(innerRatio)) return null;
        if (innerRatio >= 2.30) return 4;  // "2.35:1 (cinema)"
        if (innerRatio >= 2.15) return 1;  // "21:9 (ultrawide)"
        return null;                       // not cinema/ultrawide content
    }

    /** Try the letterbox detection, apply best-matching aspect if the
     *  viewport is at least 21:9 wide AND content has clear letterbox.
     *  Idempotent — calling twice when aspect is already set won't re-apply. */
    function autoCropIfUltrawide(adapter, entry) {
        const aspect = entry.currentAspect;
        if (aspect != null && aspect !== 0) return;  // user already picked
        const screenRatio = window.innerWidth / Math.max(1, window.innerHeight);
        if (screenRatio < 2.15) return;  // not ultrawide → don't crop 16:9

        const sample = detectLetterbox(adapter);
        if (!sample || sample.tooDark || sample.innerRatio == null) return;
        const idx = bestAspectIdxFor(sample.innerRatio);
        if (idx == null) return;

        const values = ['default', '21:9', '16:9', '4:3', '2.35:1'];
        const target = values[idx];
        entry.currentAspect = idx;
        adapter.setAspectRatio(target);
        console.info('animarr: auto-aspect →', target, {
            innerRatio: sample.innerRatio.toFixed(3),
            screenRatio: screenRatio.toFixed(3),
        });
    }

    // ── HUD controller — owns event wiring + DOM updates ──────────────
    // Takes the abstract `adapter` (PlayerAdapter contract). Phase 1 made
    // this swap-friendly: the previous version of this function depended on
    // raw Artplayer API (`art.currentTime`, `art.on('video:…')`); now every
    // playback interaction goes through the adapter so a future native
    // ExoPlayer adapter can substitute Artplayer transparently.
    function attachHud(adapter, root, entry, callbacks) {
        const hud = root.querySelector('.vp-hud');
        if (!hud) {
            console.warn('animarrPlayer: HUD root not found');
            return null;
        }
        const $ = (sel) => hud.querySelector(sel);
        const refs = {
            kicker:     $('[data-bind="kicker"]'),
            name:       $('[data-bind="name"]'),
            meta:       $('[data-bind="meta"]'),
            cur:        $('[data-bind="cur"]'),
            dur:        $('[data-bind="dur"]'),
            fill:       $('[data-bind="fill"]'),
            buffer:     $('[data-bind="buffer"]'),
            thumb:      $('[data-bind="thumb"]'),
            playIcon:   $('[data-bind="play-icon"]'),
            playLabel:  $('[data-bind="play-label"]'),
            offsetBtn:  $('[data-bind="offset-btn"]'),
            castBtn:    $('[data-bind="cast-btn"]'),
            volumeIcon: $('[data-bind="volume-icon"]'),
            fsIcon:     $('[data-bind="fs-icon"]'),
            fsLabel:    $('[data-bind="fs-label"]'),
            track:      $('[data-act="seek"]'),
            preview:     $('[data-bind="preview"]'),
            previewImg:  $('[data-bind="preview-img"]'),
            previewTime: $('[data-bind="preview-time"]'),
        };

        // ── touch / phone layout gate ─────────────────────────────────
        // The minimal mobile layout (centre play, ±10 double-tap, settings
        // gear) is driven by [data-touch="1"] on the HUD: a coarse pointer OR
        // a narrow viewport (so a resized desktop window previews it too), and
        // NEVER the Android-TV host (the d-pad needs the full button bar).
        const isTvHost = document.documentElement.classList.contains('animarr-tv-host');
        const applyTouchMode = () => {
            const mm = window.matchMedia;
            const mq = (q) => !!mm && mm(q).matches;
            // Detect a touch device robustly. The Android System WebView (MAUI
            // host) does NOT report `pointer: coarse`, so relying on that left
            // the mobile layout to `max-width:760` alone — true in portrait but
            // FALSE in landscape (~964px wide), which dropped the phone back to
            // the full desktop button bar on rotate. maxTouchPoints / ontouchstart
            // are orientation- and width-independent and work in that WebView.
            const hasTouch = (navigator.maxTouchPoints || 0) > 0
                || ('ontouchstart' in window)
                || mq('(pointer: coarse)') || mq('(any-pointer: coarse)');
            const touch = !isTvHost && (hasTouch || mq('(max-width: 760px)'));
            hud.setAttribute('data-touch', touch ? '1' : '0');
        };
        applyTouchMode();
        let touchMql = null;
        try {
            touchMql = window.matchMedia('(max-width: 760px)');
            touchMql.addEventListener('change', applyTouchMode);
        } catch { try { touchMql && touchMql.addListener(applyTouchMode); } catch {} }

        // Transient ±10 ripple shown on a double-tap seek (phone).
        function showSeekRipple(side) {
            const rip = document.createElement('div');
            rip.className = 'vp-seek-ripple vp-seek-ripple--' + side;
            rip.innerHTML = '<span>' + (side === 'left' ? '« −10' : '+10 »') + '</span>';
            // Append to the player root (sibling of .vp-hud), NOT the HUD — the
            // HUD fades to opacity:0 when controls are hidden, and a double-tap
            // seek commonly happens with controls down, where the ripple must
            // still be visible.
            root.appendChild(rip);
            setTimeout(() => { try { rip.remove(); } catch {} }, 600);
        }

        // Settings menu (mobile gear): consolidates the secondary controls into
        // one worded list. Each row fires the same callback the (now hidden)
        // bottom-bar button would, which opens its own picker popup. Rows whose
        // button is currently unavailable (offset / cast hidden) are skipped.
        function openSettingsMenu() {
            const hidden = (act) => {
                const b = hud.querySelector('[data-act="' + act + '"]');
                return !b || b.style.display === 'none';
            };
            const rows = [
                { act: 'cc',      label: 'Subtitles',    run: callbacks.cc },
                { act: 'audio',   label: 'Audio',        run: callbacks.audio },
                { act: 'quality', label: 'Quality',      run: callbacks.quality },
                { act: 'aspect',  label: 'Aspect ratio', run: callbacks.aspect },
                { act: 'offset',  label: 'Audio sync',   run: callbacks.offset },
                { act: 'volume',  label: 'Volume',       run: callbacks.volume },
                { act: 'cast',    label: 'Cast to TV',   run: callbacks.cast },
                { act: 'info',    label: 'Media info',   run: callbacks.info },
            ].filter(r => !hidden(r.act) && typeof r.run === 'function');
            openMenuPopup(root, 'settings', 'Settings', rows);
        }

        // ── auto-hide ─────────────────────────────────────────────────
        // Hide the controls after 3s with no mouse/remote activity (during
        // playback). Any mouse move, tap, button press or remote key calls
        // show(), which resets this timer.
        const HIDE_MS = 3000;
        let hideTimer = null;
        let hovering = false;
        let dragging = false;
        // How long the Skip-intro pill / Up-Next card force themselves visible
        // when they first pop up, regardless of the HUD's own state — see
        // applyFloatingVisibility().
        const ANNOUNCE_MS = 5000;
        function show() {
            hud.classList.remove('vp-hud--hidden');
            hud.setAttribute('data-visible', 'true');
            root.style.cursor = '';
            applyFloatingVisibility();
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
            // Arm the hide timer unless the user is actively interacting. We do
            // NOT gate arming on adapter.playing: play() is async, so right
            // after a click adapter.playing is still false — gating here meant
            // the timer never armed and the controls stayed forever. Instead we
            // re-check playing when the timer FIRES (3s later it's reliably
            // true), so paused playback keeps the controls up while active
            // playback hides them.
            if (hovering || dragging) return;
            hideTimer = setTimeout(() => {
                if (hovering || dragging) return;
                if (root.querySelector('.vp-hud-popup')) return;  // popup open
                if (!adapter.playing) return;                     // keep visible while paused
                hideNow();
            }, HIDE_MS);
        }
        function hideNow() {
            hud.classList.add('vp-hud--hidden');
            hud.setAttribute('data-visible', 'false');
            root.style.cursor = 'none';
            applyFloatingVisibility();
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        }
        // Skip-intro pill and the Up-Next card float above the HUD's control
        // bars. Each has two phases once its own logic (intro/credits time
        // window) says it wants to be shown:
        //   1. ANNOUNCE (first ANNOUNCE_MS): forced visible regardless of the
        //      HUD's own state — a self-contained "heads up, you can skip this"
        //      toast, so it's not missed just because the HUD happened to be
        //      auto-hidden when the window started.
        //   2. FOLLOW (after ANNOUNCE_MS, for the rest of the window): visible
        //      only while the HUD itself is — otherwise a bright pill (or the
        //      Up-Next card) is left floating alone once the controls fade.
        // skipForced/upNextForced flip the gate; setSkip/showUpNext arm the
        // ANNOUNCE_MS timer that clears them. Called whenever anything relevant
        // changes: show()/hideNow()/pause (HUD) and setSkip/hideSkip/
        // showUpNext/hideUpNext (their own want-shown state) and the two
        // announce timers (phase transition).
        function applyFloatingVisibility() {
            const hudVisible = hud.getAttribute('data-visible') === 'true';
            skipEl.classList.toggle('vp-skip--hidden', !skipShown || !(skipForced || hudVisible));
            upNextEl.classList.toggle('vp-upnext--hidden', !upNextShown || !(upNextForced || hudVisible));
        }
        // Tap-to-toggle: a tap/click on the bare video area reveals the HUD when
        // hidden and hides it when shown (YouTube-style). Controls live in
        // .vp-hud__top / .vp-hud__bottom (pointer-events:auto) and stopPropagation,
        // so taps on buttons never reach this handler.
        function toggleHud() {
            if (hud.getAttribute('data-visible') === 'true') hideNow(); else show();
        }
        // "hovering" must mean "mouse is over the CONTROL BARS" (so the auto-hide
        // timer doesn't yank them away mid-aim) — NOT "mouse is anywhere over the
        // player". Binding to .vp-hud was the bug: the full-screen tap-catcher is
        // a pointer-events:auto descendant of .vp-hud, so .vp-hud's mouseenter
        // fired for the whole video and hovering stuck true forever on desktop,
        // which blocked the hide timer from ever arming. Bind to the bars only.
        [hud.querySelector('.vp-hud__top'), hud.querySelector('.vp-hud__bottom')]
            .filter(Boolean)
            .forEach((bar) => {
                bar.addEventListener('mouseenter', () => { hovering = true;  show(); });
                bar.addEventListener('mouseleave', () => { hovering = false; show(); });
            });
        // Ignore the synthetic "ghost" mousemove the browser fires ~300ms after
        // a touch tap. On a phone that ghost called show() right as a deferred
        // tap-toggle was deciding to hide — so the HUD flashed up and instantly
        // vanished (and, on a tap-to-hide, the ghost re-revealed it). Real mouse
        // moves on desktop are untouched (no preceding touch ⇒ lastTouchAt 0).
        let lastTouchAt = 0;
        const markTouch = () => { lastTouchAt = Date.now(); };
        root.addEventListener('touchstart', markTouch, { passive: true });
        root.addEventListener('touchend', markTouch, { passive: true });
        const onActivity = () => { if (Date.now() - lastTouchAt < 700) return; show(); };
        // Desktop: mouse movement reveals the HUD (then it auto-hides).
        root.addEventListener('mousemove', onActivity);
        // Tap-catcher: a dedicated full-area layer (.vp-hud__tap, pointer-events
        // auto even while the HUD is hidden) captures taps/clicks on the bare
        // video so they don't fall through to Artplayer's built-in click-to-play.
        // The control bars sit ABOVE this layer (later in the DOM) so buttons
        // still work. Behaviour is pointer-type aware:
        //   • touch / pen (PHONE)   → tap shows/hides the controls (YouTube mobile)
        //   • mouse (DESKTOP / WEB) → click anywhere = play/pause (YouTube desktop);
        //                             the controls reveal via show() and then
        //                             auto-hide after the 3s inactivity timeout.
        // A small movement threshold ignores drags/swipes so they don't count
        // as taps.
        const tapEl = hud.querySelector('.vp-hud__tap');
        if (tapEl) {
            let downX = 0, downY = 0;
            let lastTapAt = 0, lastTapZone = '', sideTapTimer = null;
            const DT_MS = 280;   // double-tap window
            tapEl.addEventListener('pointerdown', (e) => { downX = e.clientX; downY = e.clientY; });
            tapEl.addEventListener('pointerup', (e) => {
                if (Math.hypot((e.clientX || 0) - downX, (e.clientY || 0) - downY) > 12) return;
                const openPopup = root.querySelector('.vp-hud-popup');
                if (openPopup) {
                    // Tap outside dismisses a popup — but not the very tap that
                    // opened it (touch fires a ghost click ~300ms later).
                    if (Date.now() - Number(openPopup.dataset.openedAt || 0) >= 350) openPopup.remove();
                    return;
                }
                const isTouch = (e.pointerType === 'touch' || e.pointerType === 'pen');
                // Mouse / desktop: click = play/pause, no gesture seeking.
                if (!isTouch) { togglePlay(); return; }
                // Single tap = show/hide the controls; a double tap on a side =
                // ±10s seek. The only way to tell a lone tap from the first half
                // of a double tap is to WAIT one double-tap window, so a side
                // tap's toggle is DEFERRED: "no second tap ⇒ they wanted to hide
                // the UI". The CENTRE band (where the play/pause button sits) is
                // exempt and toggles INSTANTLY, so waking the controls makes the
                // centre button reachable right away and a tap there pauses /
                // hides with no lag — and there is no ±10 in the centre anyway.
                const touchMode = hud.getAttribute('data-touch') === '1';
                const rect = tapEl.getBoundingClientRect();
                const x = (e.clientX || 0) - rect.left;
                const zone = x < rect.width * 0.30 ? 'left'
                           : x > rect.width * 0.70 ? 'right' : 'mid';
                const now = Date.now();
                if (!touchMode || zone === 'mid') {
                    if (sideTapTimer) { clearTimeout(sideTapTimer); sideTapTimer = null; }
                    lastTapAt = now; lastTapZone = zone;
                    toggleHud();
                    return;
                }
                // Second tap on the SAME side within the window → ±10 seek; this
                // also cancels the lone-tap toggle the first tap had pending.
                if ((now - lastTapAt < DT_MS) && zone === lastTapZone) {
                    if (sideTapTimer) { clearTimeout(sideTapTimer); sideTapTimer = null; }
                    lastTapAt = 0; lastTapZone = '';
                    if (zone === 'left') { seekBy(-10); showSeekRipple('left'); }
                    else                 { seekBy(+10); showSeekRipple('right'); }
                    return;
                }
                // First (maybe only) side tap: defer the toggle one window. A
                // matching second tap cancels this timer and seeks instead.
                // Capture the show/hide intent NOW so a stray show() in the gap
                // (ghost mouse event, auto-hide firing) can't invert it.
                lastTapAt = now; lastTapZone = zone;
                const wantShow = hud.getAttribute('data-visible') !== 'true';
                if (sideTapTimer) clearTimeout(sideTapTimer);
                sideTapTimer = setTimeout(() => {
                    sideTapTimer = null;
                    if (wantShow) show(); else hideNow();
                }, DT_MS);
            });
        }

        // ── immersive (Android): hide the system status / nav bars while the
        // player is in LANDSCAPE, restore them in portrait (where the HUD top
        // bar sits below the status bar). Driven by orientation so a MANUAL
        // rotate hides the bar too, not just the fullscreen button. No-op on
        // hosts without the bridge (plain browser / desktop).
        let immersiveMql = null, onOrient = null;
        if (typeof window !== 'undefined'
            && typeof window.animarrSetImmersive === 'function'
            && window.matchMedia) {
            immersiveMql = window.matchMedia('(orientation: landscape)');
            onOrient = () => {
                const landscape = immersiveMql.matches;
                try { window.animarrSetImmersive(landscape); } catch {}
                // On Android "fullscreen" is landscape (the button rotates the
                // device), so reflect that on the FS button instead of the
                // document-fullscreen state that syncFsIcon watches.
                if (refs.fsIcon)  refs.fsIcon.innerHTML    = landscape ? I.fsExit  : I.fsEnter;
                if (refs.fsLabel) refs.fsLabel.textContent = landscape ? 'Exit FS' : 'Fullscreen';
            };
            try { immersiveMql.addEventListener('change', onOrient); }
            catch { try { immersiveMql.addListener(onOrient); } catch {} }  // old WebView
            onOrient();  // apply current orientation immediately
        }

        // ── keyboard / TV remote handling ─────────────────────────────
        // We do this ourselves (rather than letting Artplayer's `hotkey: true`
        // do it) so the seek step is 10s instead of Artplayer's 5s, and so
        // media-key + Android-TV-remote codes all route here.
        function seekBy(delta) {
            const dur = adapter.duration || entry.totalDuration || 0;
            const cur = adapter.currentTime || 0;
            const next = Math.max(0, dur > 0 ? Math.min(dur, cur + delta) : cur + delta);
            adapter.currentTime = next;
            show();
        }
        function togglePlay() {
            if (adapter.playing) adapter.pause(); else adapter.play();
            show();
        }
        function onKey(e) {
            // Only intercept when the body is in player-open state.
            if (!document.body.classList.contains('player-open')) return;
            // Don't fight text inputs.
            const t = e.target;
            if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
            switch (e.key) {
                case 'ArrowLeft':  seekBy(-10); e.preventDefault(); break;
                case 'ArrowRight': seekBy(+10); e.preventDefault(); break;
                case 'ArrowUp':
                case 'ArrowDown':
                    // Don't grab vertical arrows — D-pad nav between buttons
                    // is useful when HUD is visible.
                    return;
                case ' ':
                case 'Enter':
                case 'MediaPlayPause':
                case 'Play':
                case 'Pause':
                    togglePlay();
                    e.preventDefault();
                    break;
                case 'MediaFastForward':
                    seekBy(+30);
                    e.preventDefault();
                    break;
                case 'MediaRewind':
                    seekBy(-30);
                    e.preventDefault();
                    break;
                case 'MediaTrackNext':
                    callbacks.next();
                    e.preventDefault();
                    break;
                case 'MediaTrackPrevious':
                    callbacks.prev();
                    e.preventDefault();
                    break;
                case 'MediaStop':
                case 'Escape':
                case 'BrowserBack':
                case 'GoBack':
                    callbacks.back();
                    e.preventDefault();
                    break;
                case 'f':
                case 'F':
                    toggleFullscreen(adapter);
                    e.preventDefault();
                    break;
                case 'm':
                case 'M':
                    adapter.muted = !adapter.muted;
                    e.preventDefault();
                    break;
                default:
                    return;  // don't show HUD for unrelated keys
            }
            show();
        }
        document.addEventListener('keydown', onKey, { capture: true });

        // ── button clicks ─────────────────────────────────────────────
        hud.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-act]');
            if (!btn) return;
            const act = btn.dataset.act;
            if (act === 'seek') return;
            e.stopPropagation();
            switch (act) {
                case 'back':   callbacks.back(); break;
                case 'prev':   callbacks.prev(); break;
                case 'next':   callbacks.next(); break;
                case 'play':   togglePlay(); break;
                case 'back10': seekBy(-10); break;
                case 'fwd10':  seekBy(+10); break;
                case 'aspect': callbacks.aspect(); break;
                case 'offset': callbacks.offset(); break;
                case 'cast':   callbacks.cast(); break;
                case 'volume': callbacks.volume(); break;
                case 'audio':  callbacks.audio(); break;
                case 'cc':     callbacks.cc(); break;
                case 'quality': callbacks.quality(); break;
                case 'info':   callbacks.info(); break;
                case 'settings': openSettingsMenu(); break;
                case 'fullscreen':
                    callbacks.fullscreen();
                    break;
            }
            show();
        });

        // ── progress bar drag/click ───────────────────────────────────
        function pctFromEvent(e) {
            const rect = refs.track.getBoundingClientRect();
            const cx = e.clientX != null ? e.clientX
                     : (e.touches && e.touches[0] && e.touches[0].clientX) || 0;
            return Math.max(0, Math.min(1, (cx - rect.left) / rect.width));
        }
        function seekToPct(pct) {
            const dur = adapter.duration || entry.totalDuration || 0;
            if (dur <= 0) return;
            const t = dur * pct;
            adapter.currentTime = t;
            refs.fill.style.width = (pct * 100) + '%';
            refs.thumb.style.left = (pct * 100) + '%';
            refs.cur.textContent  = formatTime(t);
        }
        // ── trickplay seek preview ────────────────────────────────────
        // Sprite-sheet thumbnail bubble over the scrubber. Data arrives via
        // setMediaSession → entry.trickplay (null → no bubble). Mouse: any
        // hover over the track; touch: while scrubbing (pointermove only
        // fires pressed on touch), hidden again when the finger lifts.
        let previewSprite = null;
        function hideSeekPreview() {
            if (refs.preview) refs.preview.classList.remove('vp-hud__preview--on');
        }
        function seekPreviewAt(clientX) {
            const tp = entry.trickplay;
            if (!refs.preview || !tp || !tp.spriteUrl || !tp.count) { hideSeekPreview(); return; }
            const dur = adapter.duration || entry.totalDuration || 0;
            if (dur <= 0) { hideSeekPreview(); return; }
            const rect = refs.track.getBoundingClientRect();
            if (rect.width <= 0) return;
            const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
            const t   = dur * pct;
            let idx = Math.floor(t / (tp.intervalSec || 10));
            idx = Math.max(0, Math.min(tp.count - 1, idx));
            const col = idx % tp.cols, row = Math.floor(idx / tp.cols);
            if (previewSprite !== tp.spriteUrl) {
                previewSprite = tp.spriteUrl;
                refs.previewImg.style.width  = tp.tileWidth + 'px';
                refs.previewImg.style.height = tp.tileHeight + 'px';
                refs.previewImg.style.backgroundImage = 'url("' + tp.spriteUrl + '")';
                // Natural sprite size keeps the tile math 1:1 — never scale it.
                refs.previewImg.style.backgroundSize =
                    (tp.cols * tp.tileWidth) + 'px ' + (tp.rows * tp.tileHeight) + 'px';
            }
            refs.previewImg.style.backgroundPosition =
                (-(col * tp.tileWidth)) + 'px ' + (-(row * tp.tileHeight)) + 'px';
            refs.previewTime.textContent = formatTime(t);
            const half = (refs.preview.offsetWidth || tp.tileWidth) / 2;
            refs.preview.style.left =
                Math.max(half, Math.min(rect.width - half, clientX - rect.left)) + 'px';
            refs.preview.classList.add('vp-hud__preview--on');
        }

        refs.track.addEventListener('pointerdown', (e) => {
            dragging = true;
            try { refs.track.setPointerCapture(e.pointerId); } catch {}
            seekPreviewAt(e.clientX);
            seekToPct(pctFromEvent(e));
            show();
        });
        refs.track.addEventListener('pointermove', (e) => {
            seekPreviewAt(e.clientX);
            if (!dragging) return;
            seekToPct(pctFromEvent(e));
            show();
        });
        refs.track.addEventListener('pointerleave', hideSeekPreview);
        const endDrag = (e) => {
            if (!dragging) return;
            dragging = false;
            try { refs.track.releasePointerCapture(e.pointerId); } catch {}
            // Touch has no hover — drop the bubble as the finger lifts. The
            // mouse re-shows it on the very next move over the track.
            hideSeekPreview();
            show();
        };
        refs.track.addEventListener('pointerup',     endDrag);
        refs.track.addEventListener('pointercancel', endDrag);

        // ── up-next overlay (end-of-episode autoplay card) ────────────
        // Appended to the player root (a sibling of .vp-hud), gated to the
        // HUD's own shown/hidden state via applyFloatingVisibility() — same
        // reasoning as the skip-intro pill. Shows once playback crosses 90%
        // (the same threshold the server uses to auto-mark "watched") AND a
        // next episode exists on disk. In the last few seconds it shows a
        // countdown and auto-advances regardless of whether the card itself is
        // currently visible; "Dismiss" cancels that for the rest of this episode.
        const upNextEl = document.createElement('div');
        upNextEl.className = 'vp-upnext vp-upnext--hidden';
        upNextEl.innerHTML = `
            <button type="button" class="vp-upnext__close tv-focus" data-act="un-dismiss" aria-label="Close">&times;</button>
            <div class="vp-upnext__eyebrow" data-bind="un-eyebrow"></div>
            <div class="vp-upnext__name" data-bind="un-name"></div>
            <div class="vp-upnext__actions">
              <button type="button" class="vp-upnext__btn vp-upnext__btn--skip tv-focus" data-act="un-skip" data-bind="un-skip">Skip credits</button>
              <button type="button" class="vp-upnext__btn vp-upnext__btn--play tv-focus" data-act="un-next">
                <span data-bind="un-play">Play next</span>
              </button>
            </div>`;
        root.appendChild(upNextEl);
        // Resting the mouse on the card while reading it must not let it vanish
        // out from under the cursor — same "hovering" latch as the HUD's bars.
        upNextEl.addEventListener('mouseenter', () => { hovering = true;  show(); });
        upNextEl.addEventListener('mouseleave', () => { hovering = false; show(); });
        const unRefs = {
            eyebrow: upNextEl.querySelector('[data-bind="un-eyebrow"]'),
            name:    upNextEl.querySelector('[data-bind="un-name"]'),
            play:    upNextEl.querySelector('[data-bind="un-play"]'),
            skip:    upNextEl.querySelector('[data-bind="un-skip"]'),
        };
        // Next-up appears at the detected credits start; with no detection it
        // falls back to this fraction of the runtime.
        const UP_NEXT_FALLBACK_PCT = 0.95;
        const UP_NEXT_COUNTDOWN = 10;   // seconds before end to start auto-advance
        let upNextShown = false, upNextDismissed = false, upNextDone = false, skipCreditsUsed = false;
        let upNextForced = false, upNextForceTimer = null;

        function upNextLabels() { return entry.upNext || {}; }
        function showUpNext() {
            if (upNextShown) return;
            upNextShown = true;
            const m = upNextLabels();
            unRefs.eyebrow.textContent = m.eyebrow || 'Up next';
            unRefs.name.textContent    = m.name || '';
            unRefs.play.textContent    = m.play || 'Play next';
            unRefs.skip.textContent    = entry.skipCreditsLabel || 'Skip credits';
            // Announce phase: force it visible for ANNOUNCE_MS regardless of the
            // HUD, then hand off to "follow the HUD" for the rest of the credits
            // (applyFloatingVisibility — see its comment).
            upNextForced = true;
            clearTimeout(upNextForceTimer);
            upNextForceTimer = setTimeout(() => {
                upNextForced = false;
                applyFloatingVisibility();
            }, ANNOUNCE_MS);
            applyFloatingVisibility();
        }
        function hideUpNext() {
            if (!upNextShown) return;
            upNextShown = false;
            upNextForced = false;
            clearTimeout(upNextForceTimer);
            applyFloatingVisibility();
        }
        function dismissUpNext() { upNextDismissed = true; hideUpNext(); }
        function triggerUpNext() {
            if (upNextDone) return;
            upNextDone = true;
            hideUpNext();
            try { callbacks.next(); } catch {}
        }
        // Called from updateProgress on every timeupdate (smooth countdown).
        function updateUpNext(cTime, dTime) {
            if (upNextDone || upNextDismissed) return;
            if (!entry.nextAvailable || !(dTime > 0)) { hideUpNext(); return; }
            // Show at the detected end-credits start; otherwise fall back to 95%.
            const cs = entry.segments && entry.segments.creditsStart;
            const triggerAt = (cs > 0 && cs < dTime) ? cs : dTime * UP_NEXT_FALLBACK_PCT;
            if (cTime < triggerAt) { hideUpNext(); return; }  // not at credits / fallback yet
            showUpNext();
            // In-card Skip-credits button: only when there's content after the
            // credits to jump to (e.g. a next-episode preview).
            const sg = entry.segments;
            const canSkip = !skipCreditsUsed && !!(sg && sg.creditsEnd > 0 && (dTime - sg.creditsEnd) > 5 && cTime < sg.creditsEnd);
            unRefs.skip.style.display = canSkip ? '' : 'none';
            const remaining = dTime - cTime;
            const baseLabel = upNextLabels().play || 'Play next';
            if (remaining <= UP_NEXT_COUNTDOWN) {
                unRefs.play.textContent = baseLabel + ' · ' + Math.max(0, Math.ceil(remaining));
                if (remaining <= 0.4) triggerUpNext();
            } else {
                unRefs.play.textContent = baseLabel;
            }
        }
        upNextEl.addEventListener('click', (e) => {
            const b = e.target.closest('[data-act]');
            if (!b) return;
            e.stopPropagation();
            if (b.dataset.act === 'un-next') triggerUpNext();
            else if (b.dataset.act === 'un-dismiss') dismissUpNext();
            else if (b.dataset.act === 'un-skip') {
                const sg = entry.segments;
                if (sg && sg.creditsEnd > 0) {
                    skipCreditsUsed = true;
                    unRefs.skip.style.display = 'none';
                    adapter.currentTime = sg.creditsEnd;
                }
            }
        });
        // Belt-and-braces: timeupdate can stop firing right at EOF, so the
        // genuine end also advances (unless the user dismissed the card).
        adapter.on('ended', () => {
            if (!upNextDismissed && entry.nextAvailable) triggerUpNext();
        });

        // ── skip-intro button ─────────────────────────────────────────
        // Floating button shown only while currentTime is inside the detected
        // intro [introStart, introEnd]; clicking seeks past it. Sibling of the
        // HUD, but gated to the HUD's own shown/hidden state via
        // applyFloatingVisibility() — it must not linger alone once the rest of
        // the controls have faded out. Stays hidden entirely when no intro was
        // detected for this episode.
        const skipEl = document.createElement('button');
        skipEl.type = 'button';
        skipEl.className = 'vp-skip vp-skip--hidden tv-focus';
        root.appendChild(skipEl);
        // Resting the mouse on the pill itself must not let it vanish out from
        // under the cursor — same "hovering" latch as the HUD's own control bars.
        skipEl.addEventListener('mouseenter', () => { hovering = true;  show(); });
        skipEl.addEventListener('mouseleave', () => { hovering = false; show(); });
        let skipShown = false, skipTarget = 0, skipIntroUsed = false, skipForced = false, skipForceTimer = null;
        function setSkip(label, target) {
            skipTarget = target;
            if (skipEl.textContent !== label) skipEl.textContent = label;
            if (!skipShown) {
                skipShown = true;
                // Announce phase: force it visible for ANNOUNCE_MS regardless of
                // the HUD, then hand off to "follow the HUD" for the rest of the
                // intro (applyFloatingVisibility — see its comment).
                skipForced = true;
                clearTimeout(skipForceTimer);
                skipForceTimer = setTimeout(() => {
                    skipForced = false;
                    applyFloatingVisibility();
                }, ANNOUNCE_MS);
                applyFloatingVisibility();
            }
        }
        function hideSkip() {
            if (!skipShown) return;
            skipShown = false;
            skipForced = false;
            clearTimeout(skipForceTimer);
            applyFloatingVisibility();
        }
        // Floating pill for Skip intro only (the opening). Skip credits now lives
        // inside the Up-Next card (the credits zone).
        function updateSkip(cTime) {
            const s = entry.segments;
            if (!skipIntroUsed && s && s.introEnd > 0 && cTime >= (s.introStart || 0) && cTime < s.introEnd) {
                setSkip(entry.skipIntroLabel || 'Skip intro', s.introEnd);
            } else {
                hideSkip();
            }
        }
        skipEl.addEventListener('click', (e) => {
            e.stopPropagation();
            // One-shot: mark used + hide immediately so the next timeupdate (which
            // still sees the pre-seek currentTime, esp. on Direct Stream reload)
            // doesn't flash the pill back on.
            if (skipTarget > 0) { skipIntroUsed = true; hideSkip(); adapter.currentTime = skipTarget; }
        });

        // ── adapter events ────────────────────────────────────────────
        function updateProgress() {
            const cTime = adapter.currentTime || 0;
            const dTime = adapter.duration || entry.totalDuration || 0;
            const pct = dTime > 0 ? (cTime / dTime) * 100 : 0;
            if (!dragging) {
                refs.fill.style.width  = pct + '%';
                refs.thumb.style.left  = pct + '%';
                refs.cur.textContent   = formatTime(cTime);
            }
            refs.dur.textContent = formatTime(dTime);
            // End-of-episode autoplay card (credits start → show; last 10s → countdown).
            updateUpNext(cTime, dTime);
            updateSkip(cTime);
            // Buffered range — only meaningful on the web adapter (raw <video>
            // exposes TimeRanges). NativeAdapter (Phase 2) returns null from
            // rawVideoElement so we just skip the buffer paint.
            const video = adapter.rawVideoElement();
            if (video && video.buffered && video.buffered.length > 0) {
                const end = video.buffered.end(video.buffered.length - 1);
                const bp = dTime > 0 ? (end / dTime) * 100 : 0;
                refs.buffer.style.width = bp + '%';
            }
        }
        adapter.on('timeupdate',     updateProgress);
        adapter.on('loadedmetadata', updateProgress);
        adapter.on('progress',       updateProgress);
        adapter.on('durationchange', updateProgress);
        adapter.on('play',  () => {
            hud.querySelectorAll('[data-bind="play-icon"]').forEach(e => { e.innerHTML = I.pause; });
            refs.playLabel.textContent = 'Pause';
            show();
        });
        adapter.on('pause', () => {
            hud.querySelectorAll('[data-bind="play-icon"]').forEach(e => { e.innerHTML = I.play; });
            refs.playLabel.textContent = 'Play';
            // Paused → keep HUD visible.
            hud.classList.remove('vp-hud--hidden');
            hud.setAttribute('data-visible', 'true');
            root.style.cursor = '';
            applyFloatingVisibility();
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        });

        // Sync the fullscreen icon with the document's actual fullscreen
        // state. Listening on document covers Esc-to-exit (which doesn't
        // fire Artplayer's own 'fullscreen' event) as well as button + hotkey.
        function syncFsIcon() {
            const fs = !!document.fullscreenElement;
            if (refs.fsIcon)  refs.fsIcon.innerHTML    = fs ? I.fsExit  : I.fsEnter;
            if (refs.fsLabel) refs.fsLabel.textContent = fs ? 'Exit FS' : 'Fullscreen';
        }
        document.addEventListener('fullscreenchange', syncFsIcon);
        syncFsIcon();

        const cleanup = () => {
            document.removeEventListener('keydown', onKey, { capture: true });
            document.removeEventListener('fullscreenchange', syncFsIcon);
            try { touchMql && touchMql.removeEventListener('change', applyTouchMode); }
            catch { try { touchMql && touchMql.removeListener(applyTouchMode); } catch {} }
            if (hideTimer) clearTimeout(hideTimer);
            clearTimeout(skipForceTimer);
            clearTimeout(upNextForceTimer);
            try { upNextEl.remove(); } catch {}
            // Restore the system bars + drop the orientation listener so other
            // (non-player) pages get the status bar back.
            if (immersiveMql && onOrient) {
                try { immersiveMql.removeEventListener('change', onOrient); }
                catch { try { immersiveMql.removeListener(onOrient); } catch {} }
            }
            try { if (typeof window.animarrSetImmersive === 'function') window.animarrSetImmersive(false); } catch {}
            try { if (typeof window.animarrSetOrientation === 'function') window.animarrSetOrientation('auto'); } catch {}
        };

        updateProgress();
        show();

        return {
            hud,
            cleanup,
            setTitle(kicker, name) {
                refs.kicker.textContent = kicker || '';
                refs.name.textContent   = name   || '';
            },
            setMeta(text) { refs.meta.textContent = text || ''; },
            setOffsetEnabled(on) {
                refs.offsetBtn.style.display = on ? '' : 'none';
            },
            setCastVisible(on) {
                refs.castBtn.style.display = on ? '' : 'none';
            },
            setVolumeIcon(volume, muted) {
                refs.volumeIcon.innerHTML = (muted || volume === 0) ? I.mute : I.volume;
            },
            // Hide prev / next when no adjacent episode exists on disk (movie,
            // first / last episode). Driven from setMediaSession's prev/next
            // availability flags. Applies to every layout, not just mobile.
            setNav(hasPrev, hasNext) {
                const p = hud.querySelector('[data-act="prev"]');
                const n = hud.querySelector('[data-act="next"]');
                if (p) p.style.display = hasPrev ? '' : 'none';
                if (n) n.style.display = hasNext ? '' : 'none';
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  attach
    // ─────────────────────────────────────────────────────────────────
    /**
     * @param {string} elementId
     * @param {object} dotnetRef
     * @param {string} mediaPath
     * @param {object} [opts]
     * @param {number} [opts.audioTrackIndex]  Index of audio stream in the source
     *                                          (0=first). Used by switchAudio to
     *                                          restart the session with a
     *                                          different audio map.
     * @param {number} [opts.forceResumeSec]   Override .NET-supplied resume
     *                                          position. Set by switchAudio so
     *                                          the new session resumes exactly
     *                                          where the old one stopped.
     */
    async function attach(elementId, dotnetRef, mediaPath, opts) {
        const el = document.getElementById(elementId);
        if (!el) {
            console.warn('animarrPlayer.attach: container not found', elementId);
            return;
        }
        if (WIRED.has(elementId)) {
            console.warn('animarrPlayer: re-attaching without detach, cleaning up first');
            detach(elementId);
        }
        const audioTrackIndex = (opts && Number.isFinite(opts.audioTrackIndex))
            ? Math.max(0, opts.audioTrackIndex) : 0;
        // Output height cap (0 = original). Below source → server downscales.
        // Carried across re-attaches by switchQuality (mirrors audioTrackIndex).
        const maxHeight = (opts && Number.isFinite(opts.maxHeight))
            ? Math.max(0, opts.maxHeight) : 0;
        // Output bitrate cap in Mbps (0 = no cap / original). Below source it
        // forces a re-encode at the chosen bitrate; carried by switchQuality.
        const maxBitrate = (opts && Number.isFinite(opts.maxBitrate))
            ? Math.max(0, opts.maxBitrate) : 0;
        const forceResumeSec  = (opts && Number.isFinite(opts.forceResumeSec))
            ? Math.max(0, opts.forceResumeSec)  : null;
        // Absolute path to an external dub audio file to mux in place of the
        // source's own audio. Carried across re-attaches by switchAudio /
        // switchQuality so changing quality doesn't silently drop the dub.
        const externalAudioPath = (opts && typeof opts.externalAudioPath === 'string' && opts.externalAudioPath)
            ? opts.externalAudioPath : null;
        if (typeof window.Artplayer !== 'function') {
            console.error('animarrPlayer: Artplayer not loaded from CDN');
            return;
        }

        // Tell tv-nav.js to suspend its spatial-nav handler while we own the
        // arrow keys for seek. Mirror class removal in detach().
        document.body.classList.add('player-open');

        const abort = new AbortController();
        const refRef = { current: dotnetRef };
        const entry = {
            abort, refRef, art: null, sessionToken: null, keepaliveTimer: null,
            totalDuration: 0, resumeOffset: 0, hud: null, clockTimer: null,
            mediaInfo: null, subtitleList: [], audioList: [],
            currentSubIdx: null, currentAudIdx: audioTrackIndex,
            currentMaxHeight: maxHeight,
            currentMaxBitrate: maxBitrate,
            // External-track state. currentExternalAudioPath != null means the
            // active audio is a sideload dub (not an in-file stream); the two
            // external lists are filled from /api/external-tracks below.
            currentExternalAudioPath: externalAudioPath,
            currentExtSubPath: null,
            externalAudioList: [], externalSubList: [],
            // Captured so switchAudio() can re-attach with a new audio index
            // without needing the .NET-side parameters again.
            mediaPath, dotnetRef,
        };
        WIRED.set(elementId, entry);

        // ── 1) Resume position ────────────────────────────────────────
        // When the caller forced a resume position (switchAudio carries over
        // current playback time), use it without round-tripping to .NET.
        let resumeSec = 0;
        if (forceResumeSec != null) {
            resumeSec = forceResumeSec;
        } else {
            try {
                const sec = await dotnetRef.invokeMethodAsync('GetResumePositionSec');
                if (typeof sec === 'number' && sec > 5) resumeSec = sec;
            } catch (e) { /* no resume info */ }
        }
        if (abort.signal.aborted) return;

        // ── 2) Audio-sync offsets ─────────────────────────────────────
        // Auto-calibration disabled 2026-05-27 — the WebAudio analyser-based
        // measure() is producing nonsense values (0 or 40ms when reality is
        // 400ms+) so it makes audio sync WORSE than the static default. The
        // helper module stays around for getCached/setManual used by the Sync
        // popup; only the calibrate() call is bypassed.
        // Defaults: HW 70ms (empirical baseline for VAAPI/NVENC re-encode),
        // SW 0ms (HEVC 10-bit stream-copy has no sensible default — user must
        // dial in via Sync slider).
        let audioOffsetMsHw = 70;
        let audioOffsetMsSw = 0;
        if (window.animarrCalibrate) {
            const hw = window.animarrCalibrate.getCached('hw');
            if (hw !== null) audioOffsetMsHw = hw;
            const sw = window.animarrCalibrate.getCached('sw');
            if (sw !== null) audioOffsetMsSw = sw;
        }
        if (abort.signal.aborted) return;

        // ── 3) Start session ──────────────────────────────────────────
        let manifestUrl = null;
        let directPlayUrl = null;
        let directStreamUrl = null;
        // HEVC decode capability — tell the server it can ship HEVC as a
        // stream-copy (Direct Stream, original quality) rather than re-encoding
        // it to H.264. Constant per browser → memoize on window. hls.js gates on
        // MediaSource.isTypeSupported; Safari/native HLS via canPlayType.
        if (window.__animarrHevcOk === undefined) {
            let ok = false, ok10 = false;
            try {
                const t   = 'video/mp4; codecs="hvc1.1.6.L93.B0"';   // HEVC Main (8-bit)
                const t10 = 'video/mp4; codecs="hvc1.2.4.L153.B0"';  // HEVC Main10 (HDR10)
                const v = document.createElement('video');
                const can = (s) =>
                       (!!window.MediaSource && !!window.MediaSource.isTypeSupported && window.MediaSource.isTypeSupported(s))
                    || (!!window.ManagedMediaSource && !!window.ManagedMediaSource.isTypeSupported && window.ManagedMediaSource.isTypeSupported(s))
                    || v.canPlayType(s) !== '';
                ok   = can(t);
                ok10 = can(t10);
            } catch (e) { ok = false; ok10 = false; }
            window.__animarrHevcOk   = ok;
            window.__animarrHevc10Ok = ok10;
        }
        const startUrl = apiUrl('/api/hls/start?path=' + encodeURIComponent(mediaPath)
            + (resumeSec > 0 ? '&seek=' + resumeSec.toFixed(2) : '')
            + '&audioOffsetHwMs=' + audioOffsetMsHw
            + '&audioOffsetSwMs=' + audioOffsetMsSw
            + (audioTrackIndex > 0 ? '&audioTrackIndex=' + audioTrackIndex : '')
            + (maxHeight > 0 ? '&maxHeight=' + maxHeight : '')
            + (maxBitrate > 0 ? '&maxBitrate=' + maxBitrate : '')
            + (window.__animarrHevcOk ? '&clientHevc=1' : '')
            + (window.__animarrHevc10Ok ? '&clientHevc10=1' : '')
            + (externalAudioPath ? '&externalAudio=' + encodeURIComponent(externalAudioPath) : ''));

        // Kick the probe off IN PARALLEL with /api/hls/start. The probe is a
        // second ffprobe on the source and is NOT needed to start playback —
        // only to populate the audio-track / subtitle menus — so overlapping it
        // with the (longer) start wait keeps its latency off the critical path.
        const probePromise = mediaPath
            ? fetch(apiUrl('/api/probe?path=' + encodeURIComponent(mediaPath)), { signal: abort.signal })
                .then(r => (r.ok ? r.json() : null)).catch(() => null)
            : Promise.resolve(null);

        for (let attempt = 0; attempt < 2; attempt++) {
            try {
                const res = await fetch(startUrl, { method: 'POST', signal: abort.signal });
                if (!res.ok) {
                    if (attempt === 0 && res.status >= 500) {
                        await new Promise(r => setTimeout(r, 500));
                        continue;
                    }
                    throw new Error('start ' + res.status);
                }
                const data = await res.json();
                entry.totalDuration = data.totalDuration || 0;
                entry.resumeOffset  = data.resumeSec    || 0;
                // Server-authoritative output info (Phase 0) — describes what
                // the player ACTUALLY receives, including post-transcode
                // codec/HDR state. Drives the right-side meta plashka so
                // re-encoded streams correctly report e.g. "H.264 SDR" even
                // when the source was HEVC HDR.
                entry.output = data.output || null;
                if (data.directPlayUrl) {
                    directPlayUrl = data.directPlayUrl;
                } else if (data.directStreamUrl) {
                    // Progressive remux (MKV → native fMP4). No HLS session.
                    directStreamUrl = data.directStreamUrl;
                } else {
                    entry.sessionToken = data.token;
                    manifestUrl = data.manifestUrl;
                }
                break;
            } catch (err) {
                if (err.name === 'AbortError') return;
                if (attempt === 1) {
                    console.error('animarrPlayer: failed to start session', err);
                    return;
                }
                await new Promise(r => setTimeout(r, 500));
            }
        }
        if (!manifestUrl && !directPlayUrl && !directStreamUrl) return;
        if (abort.signal.aborted) return;

        // ── 4) Probe ──────────────────────────────────────────────────
        let subtitleList = [];
        let audioList = [];
        let mediaInfo = null;
        if (mediaPath) {
            try {
                // Awaits the probe fetch already in flight (started in parallel
                // with /api/hls/start above) — by now it's usually done.
                const data = await probePromise;
                if (abort.signal.aborted) return;
                if (data) {
                    const streams = data.streams || [];

                    // Subtitles
                    const subs = streams.filter(s => s.codec_type === 'subtitle');
                    subtitleList = subs.map((s, idx) => {
                        const lang  = (s.tags && (s.tags.language || s.tags.LANGUAGE)) || 'und';
                        const label = (s.tags && (s.tags.title    || s.tags.TITLE))    || `Track ${idx + 1}`;
                        return {
                            name: label + (lang !== 'und' ? ` (${lang})` : ''),
                            lang,
                            url:  apiUrl('/api/subtitle?path=' + encodeURIComponent(mediaPath))
                                + '&track=' + idx + '&format=webvtt',
                            default: !!(s.disposition && s.disposition.default)
                                  || (subs.length === 1 && idx === 0),
                        };
                    });

                    // Audio
                    const auds = streams.filter(s => s.codec_type === 'audio');
                    audioList = auds.map((s, idx) => {
                        const lang  = (s.tags && (s.tags.language || s.tags.LANGUAGE)) || '';
                        const title = (s.tags && (s.tags.title    || s.tags.TITLE))    || '';
                        const codec = (s.codec_name || '').toUpperCase();
                        const ch    = s.channels || 0;
                        const chLab = ch === 1 ? 'Mono' : ch === 2 ? 'Stereo'
                                    : ch === 6 ? '5.1' : ch === 8 ? '7.1'
                                    : ch > 0 ? `${ch}ch` : '';
                        const parts = [];
                        if (lang)  parts.push(lang.toUpperCase());
                        if (codec) parts.push(codec);
                        if (chLab) parts.push(chLab);
                        if (title) parts.push(title);
                        return {
                            index: idx,
                            lang, codec, channels: ch,
                            label: parts.join(' · ') || `Track ${idx + 1}`,
                        };
                    });

                    // Video / audio media-info
                    const v = streams.find(s => s.codec_type === 'video');
                    const a = auds[0] || null;
                    if (v) {
                        const pix = (v.pix_fmt || '').toLowerCase();
                        const is10bit = pix.includes('10le') || pix.includes('10be')
                                     || (v.profile || '').toLowerCase().includes('main 10');
                        const hasDv = (v.side_data_list || []).some(sd =>
                            (sd.side_data_type || '').toUpperCase().includes('DOVI'));
                        const xfer = (v.color_transfer || '').toLowerCase();
                        const hdrFormats = [];
                        if (hasDv) hdrFormats.push('dolbyvision');
                        if (xfer === 'smpte2084') hdrFormats.push('hdr10');
                        else if (xfer === 'arib-std-b67') hdrFormats.push('hlg');
                        mediaInfo = {
                            videoCodec: v.codec_name || '',
                            width:      v.width || 0,
                            height:     v.height || 0,
                            bitDepth:   is10bit ? 10 : 8,
                            hdr:        hdrFormats[0] || 'sdr',
                            hdrFormats,
                            audioCodec:    a ? (a.codec_name || '') : '',
                            audioChannels: a ? (a.channels || 0) : 0,
                            audioLang:     audioList[0]?.lang || '',
                            playbackTier:  directPlayUrl ? 'directplay' : (directStreamUrl ? 'directstream' : 'hls'),
                        };
                        dotnetRef.invokeMethodAsync('OnPlayerMediaInfo', mediaInfo)
                            .catch(() => {});
                    }
                }
            } catch (err) {
                if (err.name !== 'AbortError') console.warn('probe failed', err);
            }
        }
        entry.mediaInfo    = mediaInfo;
        entry.subtitleList = subtitleList;
        entry.audioList    = audioList;

        // ── 4b) External (sideload) tracks ────────────────────────────
        // Dubs / sidecar subs discovered next to the video (Tier 0/1 in
        // ExternalTrackService). Non-fatal — any failure just means no
        // external entries appear in the Audio / CC pickers.
        let externalAudioList = [];
        let externalSubList   = [];
        if (mediaPath) {
            try {
                const extRes = await fetch(
                    apiUrl('/api/external-tracks?path=' + encodeURIComponent(mediaPath)),
                    { signal: abort.signal });
                if (abort.signal.aborted) return;
                if (extRes.ok) {
                    const all = await extRes.json();
                    externalAudioList = (all || []).filter(t => t.kind === 'audio');
                    externalSubList   = (all || []).filter(t => t.kind === 'subtitle');
                }
            } catch (err) {
                if (err.name !== 'AbortError') console.warn('external-tracks fetch failed', err);
            }
        }
        entry.externalAudioList = externalAudioList;
        entry.externalSubList   = externalSubList;

        // External dub sync always rides the manual 'sw' slider channel (the
        // server routes its -itsoffset through audioOffsetSwSec for every
        // plan), so surface the Sync control whenever an external audio track
        // is active — even on the TS path that otherwise has no offset knob.
        let offsetChannel = determineOffsetChannel(entry.output, mediaInfo);
        if (externalAudioPath) offsetChannel = 'sw';

        // ── 5) Instantiate player + adapter ───────────────────────────
        // Direct Stream base (no ?seek — the adapter appends one per seek). The
        // initial load seeks straight to the resume point so playback starts
        // there without spawning a second remux.
        const directStreamBase = directStreamUrl
            ? (directStreamUrl.startsWith('/') ? apiUrl(directStreamUrl) : directStreamUrl)
            : null;
        const playUrl = directPlayUrl
            ? (directPlayUrl.startsWith('/') ? apiUrl(directPlayUrl) : directPlayUrl)
            : directStreamBase
            ? directStreamBase + (resumeSec > 0 ? '&seek=' + resumeSec.toFixed(3) : '')
            : (manifestUrl.startsWith('/')   ? apiUrl(manifestUrl)   : manifestUrl);
        const isHls          = !directPlayUrl && !directStreamUrl;
        const isDirectStream = !!directStreamUrl;
        const fileExt = mediaPath.toLowerCase().split('.').pop();
        const stylePref = readStylePref();

        let art = null;
        let adapter;

        // Codec capability gate: ask Android's MediaCodecList whether the
        // device can decode whatever the server's output describes. If not,
        // fall through to Artplayer (which might still fail, but at least
        // gives us softare decode fallback through the WebView). Skipped
        // entirely on hosts where the native bridge isn't published.
        let nativeAllowed = !opts?.forceWebPlayer && await isNativeAdapterAvailable();
        if (nativeAllowed && entry.output && typeof window.animarrNativePlayer.canDecode === 'function') {
            try {
                const o = entry.output;
                const ok = await window.animarrNativePlayer.canDecode(
                    o.videoCodec || '', o.bitDepth || 0, o.hdr || '',
                    o.width || 0, o.height || 0);
                if (!ok) {
                    console.warn('animarr: device cannot decode', o.videoCodec,
                        o.bitDepth + '-bit', o.hdr, '— falling back to Artplayer');
                    nativeAllowed = false;
                }
            } catch (e) { console.warn('canDecode probe threw', e); }
        }

        if (nativeAllowed) {
            // ── Native (ExoPlayer / Android TV) path ──────────────────
            // No Artplayer: ExoPlayer renders into the TextureView the MAUI
            // host inserted at the bottom of DecorView. We inject the HUD
            // straight into the existing `vp-art` container and flip the
            // body's `data-animarr-native` attribute so the CSS in
            // animarr-player.css drops the WebView's painted background —
            // exposing the TextureView underneath.
            document.body.dataset.animarrNative = '1';
            el.innerHTML = buildHudHtml(stylePref);
            const native = new NativeAdapter(window.animarrNativePlayer, {
                container:   el,
                resumeSec:   resumeSec,
                durationSec: entry.totalDuration,
            });
            // Native runtime error → fall back to Artplayer at the same
            // position. Without this, an ExoPlayer crash mid-stream leaves
            // the user with a black screen and no recovery.
            native.on('error', () => {
                const pos = native.currentTime;
                console.warn('animarr: native error — re-attaching as web at', pos, 's');
                detach(elementId);
                setTimeout(() => attach(elementId, dotnetRef, mediaPath, {
                    audioTrackIndex: audioTrackIndex,
                    forceResumeSec:  pos,
                    forceWebPlayer:  true,
                }), 200);
            });
            await native._start(playUrl, resumeSec);
            adapter = native;
        } else {
            // ── Artplayer (browser / non-TV MAUI) path — unchanged ────
            // Initial subtitle track: prefer the user's subtitle language, then
            // the container's `default`-disposition track, then the first. Pure
            // client-side overlay — never influences the video/audio pipeline.
            const prefSubIdx = (() => {
                if (PREFS.subLang) {
                    const i = subtitleList.findIndex(s => normLang(s.lang) === PREFS.subLang);
                    if (i >= 0) return i;
                }
                const d = subtitleList.findIndex(s => s.default);
                if (d >= 0) return d;
                return subtitleList.length > 0 ? 0 : -1;
            })();
            art = new window.Artplayer({
            container: el,
            url: playUrl,
            // Direct Stream is served as fMP4 by /api/video regardless of the
            // source extension (.mkv), so force 'mp4' — never the source ext.
            type: isHls ? 'm3u8' : (isDirectStream ? 'mp4' : (fileExt || 'mp4')),
            customType: isHls ? {
                m3u8: (video, url) => {
                    if (!window.Hls || !window.Hls.isSupported()) {
                        video.src = url;
                        return;
                    }
                    // Buffer sizing. Segments stream from the loopback media proxy
                    // (http://127.0.0.1) over a normal socket — NOT the old base64
                    // JS↔.NET bridge — so the GC-storm-from-over-buffering problem
                    // is gone, and we keep a generous forward buffer to ride out
                    // transient segment-delivery / on-the-fly transcode hiccups.
                    // backBufferLength stays modest so long episodes don't pin
                    // unbounded memory.
                    const hls = new window.Hls({
                        fragLoadingTimeOut:     60000,
                        fragLoadingMaxRetry:    8,
                        manifestLoadingTimeOut: 30000,
                        levelLoadingTimeOut:    30000,
                        maxBufferHole:          4,
                        maxFragLookUpTolerance: 2,
                        nudgeOffset:            0.5,
                        nudgeMaxRetry:          10,
                        // Generous forward buffer to absorb transient stalls.
                        maxBufferLength:        90,
                        maxMaxBufferLength:     600,
                        maxBufferSize:          120 * 1000 * 1000,
                        // Keep ~60s of already-played media so small back-seeks
                        // are instant, without holding the whole episode.
                        backBufferLength:       60,
                    });
                    hls.loadSource(url);
                    hls.attachMedia(video);
                    art.hls = hls;
                    art.on('destroy', () => { try { hls.destroy(); } catch {} });
                    // Autostart reliability: Artplayer's `autoplay` fires play()
                    // ONCE at construction — but on the HLS path the manifest +
                    // first segment may still be warming up on the server then, so
                    // that single attempt no-ops and the user has to press play.
                    // Re-kick play() the moment hls.js has parsed the manifest
                    // (media genuinely ready). Swallow the promise rejection —
                    // a NotAllowedError just means the browser's autoplay policy
                    // blocked it, in which case the visible play button is the
                    // fallback (nothing more we can do without a user gesture).
                    hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                        const p = video.play();
                        if (p && typeof p.catch === 'function') p.catch(() => {});
                    });
                    hls.on(window.Hls.Events.ERROR, (evt, data) => {
                        if (data.fatal) {
                            console.error('animarr hls fatal:', data.type, data.details);
                        }
                    });
                },
            } : {},
            title: mediaPath.split(/[\/\\]/).pop(),
            autoplay: true,
            playsInline: true,
            // Restore the user's last-set volume across sessions. Saved by the
            // volume popup; defaults to 1.0 on first run.
            volume: readVolumePref(),
            setting: false,
            playbackRate: false,
            aspectRatio: false,
            screenshot: false,
            fullscreen: true,
            fullscreenWeb: false,
            pip: false,
            airplay: false,
            miniProgressBar: false,
            mutex: true,
            backdrop: false,
            // We do hotkeys ourselves so arrows = 10s (not Artplayer's 5s)
            // and TV-remote media keys all route through one handler.
            hotkey: false,
            // Match Artplayer's internal accent (loading spinner etc.) to the
            // user's/theme's accent hue — same formula as the HUD's --hud-accent.
            theme: accentThemeColor(),
            lang: 'en',
            moreVideoAttr: { crossorigin: 'anonymous' },
            // MUST be an object — newer Artplayer 5.x validates `option.subtitle`
            // and throws "require 'object' type, but got 'undefined'" when it's
            // undefined (i.e. media with NO subtitle tracks). Use {} for the
            // no-subtitle case instead of undefined.
            subtitle: subtitleList.length > 0 ? {
                url: subtitleList[prefSubIdx >= 0 ? prefSubIdx : 0].url,
                // Artplayer accepts 'vtt' | 'srt' | 'ass'. 'webvtt' is NOT
                // a recognised value and silently dropped on the loader path
                // — fix landed 2026-05-27 after subtitle.switch() reported
                // no-op effect on every track change.
                type: 'vtt',
                encoding: 'utf-8',
                escape: false,
                // User's subtitle size (px). Omitted (Artplayer default) when 0.
                ...(PREFS.subSize > 0 ? { style: { fontSize: PREFS.subSize + 'px' } } : {}),
            } : {},
            layers: [{
                name: 'animarr-hud',
                html: buildHudHtml(stylePref),
                style: { position: 'absolute', inset: '0', zIndex: '30',
                         pointerEvents: 'none' },
            }],
            });
            adapter = new ArtplayerAdapter(art);
            // Direct Stream: route seeks through a remux-reload (the source has
            // no Range) and read duration off the server total, not the live
            // stream (whose video.duration is Infinity).
            if (isDirectStream) adapter.enableDirectStream(directStreamBase, entry.totalDuration);
        }

        entry.art = art;
        entry.adapter = adapter;
        entry.currentSubIdx = (typeof prefSubIdx === 'number') ? prefSubIdx
            : (subtitleList.findIndex(s => s.default));
        if (entry.currentSubIdx < 0 && subtitleList.length > 0) entry.currentSubIdx = 0;

        // ── Preferred audio language — HLS transcode path ONLY ──────────────
        // Direct Play / Direct Stream serve the raw container and the browser
        // plays its default audio track; switching to a non-default track there
        // would require a transcode, so we deliberately DON'T touch those paths
        // (Direct Play is never sacrificed for an audio-language preference).
        // On a transcoding HLS session we're re-muxing anyway, so re-selecting
        // the matching-language stream is free. Runs once per user-initiated
        // open (opts.autoAudio) — never on switchAudio/switchQuality re-attaches.
        if (opts && opts.autoAudio && entry.sessionToken && PREFS.audioLang
            && Array.isArray(audioList) && audioList.length > 1) {
            const want = audioList.findIndex(a => normLang(a.lang) === PREFS.audioLang);
            if (want > 0 && want !== entry.currentAudIdx) {
                // Defer past the rest of attach()'s wiring (autostart + HUD)
                // before we tear the session down and restart with the preferred
                // audio map. Only reached on a transcoding HLS file whose default
                // audio isn't the preferred language, so the extra warm-up is
                // rare; guard against the user navigating away meanwhile.
                setTimeout(() => {
                    if (WIRED.has(elementId)) {
                        try { switchAudioTrack(elementId, want); } catch {}
                    }
                }, 600);
            }
        }

        // Autostart reliability (covers BOTH the HLS and Direct-Play paths).
        // Artplayer's autoplay can lose the race against media readiness, so we
        // also kick play() on the first `canplay` of the underlying <video>.
        // One-shot; harmless if playback already started. Rejections (autoplay
        // policy) are swallowed — the play button is the fallback.
        try {
            const vEl = adapter.rawVideoElement && adapter.rawVideoElement();
            if (vEl && !vEl.__animarrAutostart) {
                vEl.__animarrAutostart = true;
                const kick = () => {
                    const p = vEl.play();
                    if (p && typeof p.catch === 'function') p.catch(() => {});
                };
                if (vEl.readyState >= 3) kick();        // already ready
                else vEl.addEventListener('canplay', kick, { once: true });
            }
        } catch {}

        // ── 5b) Wire HUD ──────────────────────────────────────────────
        // Root element for HUD events: Artplayer's player wrapper on the web
        // path, the bare `vp-art` container on the native path (where the
        // HUD HTML was injected directly above).
        const hudRoot = art ? (art.template.$player || art.container) : el;
        const hudCtl = attachHud(adapter, hudRoot, entry, {
            back:  () => refRef.current?.invokeMethodAsync('ClosePlayerFromJs').catch(() => {}),
            prev:  () => refRef.current?.invokeMethodAsync('InvokePrev').catch(() => {}),
            next:  () => refRef.current?.invokeMethodAsync('InvokeNext').catch(() => {}),
            cast:  () => openCastPopup(hudRoot, 'cast', mediaPath, adapter),
            volume: () => openVolumePopup(hudRoot, 'volume', adapter, (v) => {
                hudCtl?.setVolumeIcon(v, adapter.muted);
            }),
            aspect: () => {
                const items   = ['Default', '21:9 (ultrawide)', '16:9', '4:3', '2.35:1 (cinema)'];
                const values  = ['default',  '21:9',             '16:9', '4:3', '2.35:1'];
                const cur     = entry.currentAspect ?? 0;
                openPickerPopup(hudRoot, 'aspect', 'Aspect ratio', items, cur, (i) => {
                    entry.currentAspect = i;
                    adapter.setAspectRatio(values[i]);
                });
            },
            offset: () => {
                if (!offsetChannel || !window.animarrCalibrate) return;
                openOffsetPopup(hudRoot, 'offset', offsetChannel,
                    entry.currentExternalAudioPath ? 'External dub sync' : null);
            },
            audio: () => {
                const aopts = buildAudioOptions(entry);
                if (aopts.length === 0) return;
                const curIdx = Math.max(0, aopts.findIndex(o => o.current));
                openPickerPopup(hudRoot, 'audio', 'Audio tracks',
                    aopts.map(o => o.label), curIdx,
                    (i) => {
                        const o = aopts[i];
                        if (!o || o.current) return;
                        // No live switching with our single-stream transcode —
                        // tear the session down and restart, mapping either the
                        // chosen in-file stream (`-map 0:a:N`) or the external
                        // dub file (`-map 1:a:0`), carrying the position over.
                        switchAudio(elementId, {
                            audioTrackIndex:   o.kind === 'embedded' ? o.index : 0,
                            externalAudioPath: o.kind === 'external' ? o.path : null,
                        });
                    });
            },
            cc: () => {
                // Embedded subtitle streams first, then sidecar subtitle files
                // (labelled with their extension). Selection is tracked by
                // currentSubIdx (embedded) OR currentExtSubPath (external).
                const emb = entry.subtitleList   || [];
                const ext = entry.externalSubList || [];
                const items = ['Off',
                    ...emb.map(s => s.name),
                    ...ext.map(s => s.label + ' (' + s.ext + ')')];
                let cur = 0;
                if (entry.currentExtSubPath != null) {
                    const ei = ext.findIndex(s => s.path === entry.currentExtSubPath);
                    if (ei >= 0) cur = 1 + emb.length + ei;
                } else if (entry.currentSubIdx != null) {
                    cur = entry.currentSubIdx + 1;
                }
                openPickerPopup(hudRoot, 'cc', 'Subtitles', items, cur, (i) => {
                    if (i === 0) {
                        adapter.setSubtitle(null);
                        entry.currentSubIdx = null;
                        entry.currentExtSubPath = null;
                        return;
                    }
                    const idx = i - 1;
                    if (idx < emb.length) {
                        const s = emb[idx];
                        adapter.setSubtitle({ url: s.url, name: s.name, type: 'vtt' });
                        entry.currentSubIdx = idx;
                        entry.currentExtSubPath = null;
                    } else {
                        // Sidecar subtitle file → /api/subtitle converts it to
                        // WebVTT on the fly (a standalone .srt/.ass exposes its
                        // single subtitle stream as 0:s:0).
                        const s = ext[idx - emb.length];
                        const url = apiUrl('/api/subtitle?path=' + encodeURIComponent(s.path)
                            + '&track=0&format=webvtt');
                        adapter.setSubtitle({ url, name: s.label, type: 'vtt' });
                        entry.currentSubIdx = null;
                        entry.currentExtSubPath = s.path;
                    }
                });
            },
            quality: () => {
                // One list, three kinds of entries (single-select picker):
                //   • Original — stream-copy, no re-encode (lossless).
                //   • Resolution rungs below source — downscale + re-encode at
                //     the auto bitrate (shown in the label).
                //   • Bitrate caps — re-encode at SOURCE resolution, capped at
                //     N Mbps (trim bandwidth without dropping resolution).
                const srcH = (entry.mediaInfo && entry.mediaInfo.height)
                          || (entry.output && entry.output.height) || 0;
                const autoMbps = (h) => h <= 480 ? 2.5 : h <= 720 ? 6 : h <= 1080 ? 12 : h <= 1440 ? 24 : 40;
                const items = [{ label: 'Original' + (srcH ? ' · ' + srcH + 'p' : ''), h: 0, b: 0 }];
                [1440, 1080, 720, 480].forEach(r => {
                    if (srcH === 0 || r < srcH) items.push({ label: r + 'p · ~' + autoMbps(r) + ' Mbps', h: r, b: 0 });
                });
                // Bitrate-cap presets; ceiling scaled to the source resolution so
                // a 1080p file doesn't offer 200 Mbps but a 4K file does.
                const brCeil = srcH >= 2160 ? 200 : srcH >= 1440 ? 120 : srcH >= 1080 ? 40 : srcH >= 720 ? 25 : srcH > 0 ? 16 : 200;
                [6, 10, 16, 25, 40, 80, 120, 200].forEach(b => {
                    if (b <= brCeil) items.push({ label: '≤ ' + b + ' Mbps' + (srcH ? ' · ' + srcH + 'p' : ''), h: 0, b: b });
                });
                const curH = entry.currentMaxHeight || 0;
                const curB = entry.currentMaxBitrate || 0;
                let curIdx = items.findIndex(o => o.h === curH && o.b === curB);
                if (curIdx < 0) curIdx = 0;
                openPickerPopup(hudRoot, 'quality', 'Quality', items.map(o => o.label), curIdx, (i) => {
                    const it = items[i];
                    if (it.h === (entry.currentMaxHeight || 0) && it.b === (entry.currentMaxBitrate || 0)) return;
                    switchQuality(elementId, it.h, it.b);
                });
            },
            info: () => openInfoPopup(hudRoot, 'info', entry.infoLines || []),
            fullscreen: () => toggleFullscreen(adapter),
        });
        entry.hud = hudCtl;

        // Hide buttons that don't apply to current playback.
        hudCtl.setOffsetEnabled(!!offsetChannel);
        // Initial volume icon + wire the mute toggle (hotkey 'm' or external
        // changes) to keep the icon in sync.
        hudCtl.setVolumeIcon(adapter.volume, adapter.muted);
        adapter.on('volumechange', () => {
            hudCtl.setVolumeIcon(adapter.volume, adapter.muted);
            saveVolumePref(adapter.volume);
        });
        // Cast: hide when running inside MAUI on a TV (the user is already
        // ON the TV). MAUI index.html adds `.animarr-tv-host` to <html> when
        // its UA/viewport heuristic triggers. Browser/phone keep the button.
        const isTv = document.documentElement.classList.contains('animarr-tv-host');
        hudCtl.setCastVisible(!isTv);

        // Initial title (overwritten by setMediaSession from .NET).
        hudCtl.setTitle('', mediaPath.split(/[\/\\]/).pop());

        // Top-right meta line — describes what the PLAYER receives, not what's
        // on disk. Sourced from `data.output` returned by /api/hls/start
        // (Phase 0). For Direct Play and stream-copy paths it mirrors the
        // source; for re-encode it reports the post-transcode state (e.g. a
        // HEVC HDR DV source served via VAAPI reads "H.264 8-bit SDR" here).
        // Refreshed every 30s so the wall-clock stays current.
        function updateMeta() {
            // Top-right meta line is now EMPTY — the wall-clock was removed per
            // request, and the resolution / codec / bit-depth / HDR / audio /
            // playback-path tags moved into the bottom "Info" button popup
            // (entry.infoLines).
            hudCtl.setMeta('');

            const o = entry.output;
            const lines = [];
            if (o) {
                if (o.height) lines.push('Resolution: ' + o.height + 'p');
                const vparts = [];
                if (o.videoCodec) vparts.push(o.videoCodec.toUpperCase());
                if (o.bitDepth >= 10) vparts.push('10-bit');
                if (vparts.length) lines.push('Video: ' + vparts.join(' '));
                // The Info block reports what's actually ON SCREEN, not the
                // source's flags. No browser renders Dolby Vision — the web
                // player plays the HDR10/HLG base layer — so drop the DV tag on
                // the Artplayer path. Native ExoPlayer (art == null) keeps it:
                // DV-capable TVs do render it.
                let hdrFormats = o.hdrFormats || [];
                if (art) hdrFormats = hdrFormats.filter(f => f !== 'dolbyvision');
                const hdrs = hdrFormats.map(fmt =>
                    fmt === 'dolbyvision' ? 'Dolby Vision'
                  : fmt === 'hdr10' ? 'HDR10'
                  : fmt === 'hlg' ? 'HLG' : fmt.toUpperCase());
                if (hdrs.length) lines.push('HDR: ' + hdrs.join(', '));
                if (o.audioCodec) {
                    const ch = o.audioChannels || 0;
                    const chLabel = ch === 1 ? 'Mono' : ch === 2 ? 'Stereo'
                                  : ch === 6 ? '5.1' : ch === 8 ? '7.1'
                                  : ch > 0 ? ch + 'ch' : '';
                    let a = o.audioCodec.toUpperCase() + (chLabel ? ' ' + chLabel : '');
                    if (o.audioLanguage) a += ' (' + o.audioLanguage.toUpperCase() + ')';
                    lines.push('Audio: ' + a);
                }
                // When an external dub is muxed in, `o.audioCodec` still
                // describes the SOURCE audio (the server probes the source),
                // so add an explicit line for the active sideload track.
                if (entry.currentExternalAudioPath) {
                    const exo = (entry.externalAudioList || [])
                        .find(t => t.path === entry.currentExternalAudioPath);
                    lines.push('Dub: ' + (exo ? exo.label + ' (' + exo.ext + ')' : 'External file'));
                }
                const planTag = {
                    'directplay':     'Direct Play',
                    'directstream':   'Direct Stream · remux',
                    'ts-copy':        'HLS · TS stream-copy',
                    'vaapi-reencode': 'HLS · VAAPI → H.264',
                    'nvenc-reencode': 'HLS · NVENC → H.264',
                    'fmp4-copy':      'HLS · fMP4 stream-copy',
                }[o.plan] || 'HLS';
                lines.push('Playback: ' + planTag);
                if (o.transcoded && o.transcodeReason) lines.push('Reason: ' + o.transcodeReason);
            }
            entry.infoLines = lines;
            // If the Info popup is open, refresh its rows live (the 30s clock
            // tick also re-runs this).
            const openInfo = hudRoot.querySelector('.vp-hud-popup--info .vp-hud-popup__list');
            if (openInfo) {
                openInfo.innerHTML = (lines.length ? lines : ['No media info available'])
                    .map(l => `<div class="vp-hud-popup__info">${escapeHtml(l)}</div>`).join('');
            }
        }
        updateMeta();
        entry.clockTimer = setInterval(updateMeta, 30000);

        // ── 5c) MediaSession + PiP ────────────────────────────────────
        try {
            if ('mediaSession' in navigator) {
                const ms = navigator.mediaSession;
                ms.setActionHandler('play',     () => adapter.play());
                ms.setActionHandler('pause',    () => adapter.pause());
                ms.setActionHandler('seekto', d => {
                    if (typeof d.seekTime === 'number') adapter.currentTime = d.seekTime;
                });
                ms.setActionHandler('seekbackward', d => {
                    const step = (d && d.seekOffset) || 10;
                    adapter.currentTime = Math.max(0, adapter.currentTime - step);
                });
                ms.setActionHandler('seekforward', d => {
                    const step = (d && d.seekOffset) || 10;
                    adapter.currentTime = adapter.currentTime + step;
                });
                ms.setActionHandler('stop', () => adapter.pause());
                adapter.on('timeupdate', () => {
                    if (!('setPositionState' in ms)) return;
                    const dur = entry.totalDuration || adapter.duration || 0;
                    if (!Number.isFinite(dur) || dur <= 0) return;
                    try {
                        ms.setPositionState({
                            duration:     dur,
                            // No playbackRate getter on the adapter (we don't
                            // expose rate control) — default to 1.0.
                            playbackRate: 1,
                            position:     Math.min(adapter.currentTime, dur),
                        });
                    } catch {}
                });
                adapter.on('play',  () => { ms.playbackState = 'playing'; });
                adapter.on('pause', () => { ms.playbackState = 'paused';  });
            }
        } catch (e) { console.warn('mediaSession wire-up failed', e); }

        try {
            const video = adapter.rawVideoElement();
            if (video && document.pictureInPictureEnabled && !video.disablePictureInPicture) {
                const onVisibility = () => {
                    if (document.visibilityState === 'hidden' &&
                        !video.paused &&
                        document.pictureInPictureElement !== video) {
                        video.requestPictureInPicture().catch(() => {});
                    }
                };
                document.addEventListener('visibilitychange', onVisibility);
                entry._pipVisibilityHandler = onVisibility;
            }
        } catch {}

        // ── 6) Resume seek + progress reporting ───────────────────────
        // Direct Stream bakes the resume point into the initial URL (?seek=),
        // so seeking again here would spawn a redundant remux — skip it.
        if (resumeSec > 0 && !isDirectStream) {
            adapter.once('loadedmetadata', () => {
                adapter.currentTime = resumeSec;
            });
        }

        // Auto-aspect on ultrawide screens. Runs ~600ms after the first
        // decoded frame to give the decoder time to render real content
        // (some files start on a few black frames). If that frame is
        // also too dark to measure, retry once at ~3s for movies with
        // a long black studio-logo intro.
        adapter.once('loadeddata', () => {
            setTimeout(() => {
                autoCropIfUltrawide(adapter, entry);
                if (entry.currentAspect == null) {
                    setTimeout(() => autoCropIfUltrawide(adapter, entry), 4000);
                }
            }, 600);
        });

        let lastTick = 0;
        let lastPosForDelta = resumeSec;
        const sendProgress = () => {
            const r = refRef.current;
            if (!r) return;
            const pos = adapter.currentTime || 0;
            const dur = entry.totalDuration || adapter.duration || 0;
            let delta = 0;
            if (adapter.playing && pos > lastPosForDelta && (pos - lastPosForDelta) < 30) {
                delta = Math.round(pos - lastPosForDelta);
            }
            lastPosForDelta = pos;
            r.invokeMethodAsync('OnPlayerProgress', pos, dur, delta).catch(() => {});

            const wn = entry.watchNext;
            if (wn && typeof window.animarrWatchNextUpsert === 'function') {
                const posMs = Math.round(pos * 1000);
                const durMs = Math.round(dur * 1000);
                window.animarrWatchNextUpsert(wn.mediaId, wn.title, wn.posterUrl, posMs, durMs);
            }
        };
        adapter.on('timeupdate', () => {
            const now = Date.now();
            if (now - lastTick < 5000) return;
            lastTick = now;
            sendProgress();
        });
        adapter.on('pause', sendProgress);
        adapter.on('ended', sendProgress);

        // ── 7) Keepalive (HLS only) ───────────────────────────────────
        if (entry.sessionToken) {
            entry.keepaliveTimer = setInterval(() => {
                if (!entry.sessionToken) return;
                fetch(apiUrl('/api/hls/keepalive?token=' + encodeURIComponent(entry.sessionToken)),
                    { method: 'POST', signal: abort.signal })
                    .catch(() => {});
            }, 30000);
        }
    }

    function flush(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return;
        entry.adapter.pause();
    }

    function detach(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry) return;
        entry.refRef.current = null;
        if (entry.keepaliveTimer) { clearInterval(entry.keepaliveTimer); entry.keepaliveTimer = null; }
        if (entry.clockTimer)     { clearInterval(entry.clockTimer);     entry.clockTimer     = null; }
        if (entry.hud && entry.hud.cleanup) { try { entry.hud.cleanup(); } catch {} }
        entry.abort.abort();
        if (entry.sessionToken) {
            try {
                fetch(apiUrl('/api/hls/' + encodeURIComponent(entry.sessionToken)),
                    { method: 'DELETE', keepalive: true });
            } catch {}
        }
        if (entry._pipVisibilityHandler) {
            document.removeEventListener('visibilitychange', entry._pipVisibilityHandler);
            entry._pipVisibilityHandler = null;
        }
        try {
            if ('mediaSession' in navigator) {
                navigator.mediaSession.metadata = null;
                navigator.mediaSession.playbackState = 'none';
                ['play','pause','seekto','seekbackward','seekforward','stop',
                 'previoustrack','nexttrack'].forEach(a => {
                    try { navigator.mediaSession.setActionHandler(a, null); } catch {}
                });
            }
        } catch {}
        if (entry.adapter) {
            entry.adapter.destroy();
            entry.adapter = null;
        }
        entry.art = null;
        document.body.classList.remove('player-open');
        // Native-playback attribute drives the CSS rule that wipes body bg
        // so the TextureView shows through. Clear it on detach so other
        // routes (non-player pages) paint their normal dark chrome.
        try { delete document.body.dataset.animarrNative; } catch {}
        WIRED.delete(elementId);
    }

    /**
     * Switch to a different audio track in the source. Tears down the current
     * HLS session and starts a fresh one with the new `audioTrackIndex` baked
     * into ffmpeg's -map. The current playback position is carried over as
     * the resume point so playback continues from the same instant — albeit
     * with a 1-3s gap while the new session warms up.
     *
     * For Direct Play sessions (no sessionToken) the same dance applies — the
     * URL doesn't change but the HUD's currentAudIdx state and any future
     * audio-track-based logic should reflect the new selection.
     */
    async function switchAudio(elementId, sel) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return;
        const pos       = entry.adapter.currentTime || 0;
        const dotnetRef = entry.dotnetRef;
        const mediaPath = entry.mediaPath;
        if (!dotnetRef || !mediaPath) return;
        // Preserve the current quality cap across an audio switch (the old
        // by-index helper dropped it, reverting to source resolution).
        const maxHeight = entry.currentMaxHeight || 0;
        const maxBitrate = entry.currentMaxBitrate || 0;
        // Tear down the old session synchronously — detach() handles HLS
        // DELETE + Artplayer destroy + WIRED cleanup.
        detach(elementId);
        // Brief delay so the server has a tick to clean up the old ffmpeg
        // process / tmp dir before we ask for a new one (avoids racing the
        // dedup-by-source-path step inside StartAsync).
        await new Promise(r => setTimeout(r, 100));
        await attach(elementId, dotnetRef, mediaPath, {
            audioTrackIndex:   (sel && Number.isFinite(sel.audioTrackIndex)) ? sel.audioTrackIndex : 0,
            externalAudioPath: (sel && sel.externalAudioPath) || null,
            maxHeight,
            maxBitrate,
            forceResumeSec:    pos,
        });
    }

    // Back-compat thin wrapper: switch to an in-file audio stream by index
    // (clears any active external dub).
    async function switchAudioTrack(elementId, audioTrackIndex) {
        return switchAudio(elementId, { audioTrackIndex, externalAudioPath: null });
    }

    /**
     * Switch output quality (max height cap). Tears down + restarts the HLS
     * session with a new `maxHeight` (server downscales/re-encodes below the
     * source height), carrying current position + audio track over. 0 = original.
     * Same teardown+resume dance as switchAudioTrack (1-3s warm-up gap).
     */
    async function switchQuality(elementId, maxHeight, maxBitrate) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return;
        const pos       = entry.adapter.currentTime || 0;
        const dotnetRef = entry.dotnetRef;
        const mediaPath = entry.mediaPath;
        const audioTrackIndex = entry.currentAudIdx || 0;
        if (!dotnetRef || !mediaPath) return;
        detach(elementId);
        await new Promise(r => setTimeout(r, 100));
        await attach(elementId, dotnetRef, mediaPath, {
            audioTrackIndex,
            // Keep the active external dub across a quality change.
            externalAudioPath: entry.currentExternalAudioPath || null,
            maxHeight,
            maxBitrate: maxBitrate || 0,
            forceResumeSec: pos,
        });
    }

    function setMediaSession(elementId, meta, handlers) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return;
        if (entry.hud) {
            const kicker = meta?.hudKicker || '';
            const name   = meta?.hudName   || meta?.title || '';
            entry.hud.setTitle(kicker, name);
        }
        // Up-next overlay data (end-of-episode autoplay card). Read live by the
        // HUD's updateUpNext on each timeupdate; stored on the entry so it
        // survives until the next setMediaSession call (i.e. the next episode).
        entry.nextAvailable = !!(meta && meta.nextAvailable);
        if (entry.hud && entry.hud.setNav) {
            entry.hud.setNav(!!(meta && meta.prevAvailable), !!(meta && meta.nextAvailable));
        }
        entry.upNext = {
            eyebrow: (meta && meta.upNextEyebrow) || 'Up next',
            name:    (meta && meta.upNextName)    || '',
            play:    (meta && meta.upNextPlay)    || 'Play next',
            dismiss: (meta && meta.upNextDismiss) || 'Dismiss',
        };
        // Skip-intro/credits segment times (seconds). Read live by updateUpNext
        // (credits → next-up trigger) and updateSkipIntro (Skip button) on each
        // timeupdate. null → no detected segments: Skip stays hidden and the
        // next-up card falls back to 95% of the runtime.
        entry.segments = (meta && meta.segments) || null;
        // Trickplay sprite manifest (seek preview). Read live by the HUD's
        // scrubber hover/drag handler; null → no preview bubble. Warm the
        // sprite image so the first hover doesn't flash an empty box.
        entry.trickplay = (meta && meta.trickplay) || null;
        try {
            if (entry.trickplay && entry.trickplay.spriteUrl) {
                const im = new Image();
                im.src = entry.trickplay.spriteUrl;
            }
        } catch { /* preload is best-effort */ }
        entry.skipIntroLabel   = (meta && meta.skipIntroLabel)   || entry.skipIntroLabel   || 'Skip intro';
        entry.skipCreditsLabel = (meta && meta.skipCreditsLabel) || entry.skipCreditsLabel || 'Skip credits';
        try {
            if (!('mediaSession' in navigator)) return;
            const ms = navigator.mediaSession;
            const artwork = [];
            if (meta && meta.artworkUrl) {
                artwork.push({ src: meta.artworkUrl, sizes: '512x512', type: 'image/jpeg' });
                artwork.push({ src: meta.artworkUrl, sizes: '256x256', type: 'image/jpeg' });
                artwork.push({ src: meta.artworkUrl, sizes: '96x96',   type: 'image/jpeg' });
            }
            ms.metadata = new MediaMetadata({
                title:  (meta && meta.title)  || 'Animarr',
                artist: (meta && meta.artist) || '',
                album:  (meta && meta.album)  || '',
                artwork,
            });
            if (handlers && handlers.dotnetRef) {
                if (meta && meta.prevAvailable) {
                    ms.setActionHandler('previoustrack', () => {
                        try { handlers.dotnetRef.invokeMethodAsync('InvokePrev'); } catch {}
                    });
                } else {
                    try { ms.setActionHandler('previoustrack', null); } catch {}
                }
                if (meta && meta.nextAvailable) {
                    ms.setActionHandler('nexttrack', () => {
                        try { handlers.dotnetRef.invokeMethodAsync('InvokeNext'); } catch {}
                    });
                } else {
                    try { ms.setActionHandler('nexttrack', null); } catch {}
                }
            }
        } catch (e) { console.warn('setMediaSession failed', e); }
    }

    function setWatchNextMeta(elementId, meta) {
        const entry = WIRED.get(elementId);
        if (!entry) return;
        if (!meta || !meta.mediaId || !meta.title) return;
        entry.watchNext = {
            mediaId:   String(meta.mediaId),
            title:     String(meta.title),
            posterUrl: meta.posterUrl ? String(meta.posterUrl) : '',
        };
    }

    async function togglePictureInPicture(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return false;
        const video = entry.adapter.rawVideoElement();
        if (!video) return false;
        try {
            if (document.pictureInPictureElement === video) {
                await document.exitPictureInPicture();
                return false;
            }
            if (document.pictureInPictureEnabled && !video.disablePictureInPicture) {
                await video.requestPictureInPicture();
                return true;
            }
        } catch (e) { console.warn('PiP toggle failed', e); }
        return false;
    }

    /**
     * Update the HUD style live (called from ProfilePanel when the user
     * flips the "icons only" toggle while the player is open). If no player
     * is currently mounted, this is a no-op — next attach() picks up the
     * new preference via readStylePref().
     */
    function setStyle(style) {
        if (style !== 'icons' && style !== 'full') return;
        try { localStorage.setItem('animarr_player_style', style); } catch {}
        // Update any live HUDs.
        WIRED.forEach(entry => {
            const hud = entry.hud && entry.hud.hud;
            if (hud) hud.setAttribute('data-style', style);
        });
    }

    window.animarrPlayer = {
        attach, flush, detach,
        setMediaSession, setWatchNextMeta, togglePictureInPicture,
        setStyle, switchAudioTrack, switchAudio, setPrefs,
    };
})();

// ── Theme music (anime OP/ED) — soft autoplay on the detail page ─────────────
// A tiny standalone controller (kept out of the Artplayer entry map): one
// <audio> element, faded in/out. Driven by MediaDetail.razor via
// animarrTheme.play(url, volumePct) / animarrTheme.stop(). Browsers gate
// autoplay-with-sound behind a user gesture; entering a title is itself a
// click, so it usually starts — and if the very first play() is blocked we
// retry on the next pointerdown/keydown. Never throws into Blazor interop.
(function () {
    let audio = null;
    let fadeTimer = null;
    let curUrl = null;
    let pendingResume = null;

    function clearFade() {
        if (fadeTimer) { clearInterval(fadeTimer); fadeTimer = null; }
    }

    function fadeTo(el, target, ms, onDone) {
        if (!el) return;
        clearFade();
        const steps = Math.max(1, Math.round(ms / 50));
        const start = el.volume;
        const delta = (target - start) / steps;
        let i = 0;
        fadeTimer = setInterval(function () {
            i++;
            if (audio !== el) { clearFade(); return; }   // superseded by a newer theme
            el.volume = Math.max(0, Math.min(1, start + delta * i));
            if (i >= steps) {
                clearFade();
                el.volume = Math.max(0, Math.min(1, target));
                if (onDone) onDone();
            }
        }, 50);
    }

    function detachResume() {
        if (!pendingResume) return;
        window.removeEventListener('pointerdown', pendingResume, true);
        window.removeEventListener('keydown', pendingResume, true);
        pendingResume = null;
    }

    window.animarrTheme = {
        // Start (or keep) the theme. volumePct 0..100. Idempotent for the same url.
        play: function (url, volumePct) {
            try {
                if (!url) return;
                const target = Math.max(0, Math.min(1, (volumePct == null ? 40 : volumePct) / 100));
                if (audio && curUrl === url) {           // already on this theme — just match volume
                    fadeTo(audio, target, 400);
                    return;
                }
                window.animarrTheme.stop();
                curUrl = url;
                const el = new Audio(url);                // no crossOrigin: plain media playback works cross-origin
                el.loop = true;
                el.volume = 0;
                audio = el;
                const tryPlay = function () {
                    const p = el.play();
                    if (p && p.then) {
                        p.then(function () { detachResume(); fadeTo(el, target, 800); })
                         .catch(function () {
                             if (audio !== el) return;     // stopped meanwhile
                             detachResume();
                             pendingResume = function () {
                                 detachResume();
                                 if (audio === el) el.play().then(function () { fadeTo(el, target, 800); }).catch(function () {});
                             };
                             window.addEventListener('pointerdown', pendingResume, true);
                             window.addEventListener('keydown', pendingResume, true);
                         });
                    }
                };
                tryPlay();
            } catch (e) { /* never throw into Blazor interop */ }
        },
        // Fade out + tear down.
        stop: function () {
            curUrl = null;
            clearFade();
            detachResume();
            const el = audio;
            audio = null;
            if (!el) return;
            try {
                const steps = 6, start = el.volume; let i = 0;
                const t = setInterval(function () {
                    i++;
                    el.volume = Math.max(0, start - (start / steps) * i);
                    if (i >= steps) { clearInterval(t); try { el.pause(); el.src = ''; } catch (e) {} }
                }, 40);
            } catch (e) { try { el.pause(); } catch (e2) {} }
        }
    };
})();
