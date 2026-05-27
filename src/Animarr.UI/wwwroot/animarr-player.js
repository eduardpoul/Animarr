// Artplayer-based player bridge — variant B (full custom HUD).
//
// Artplayer's built-in chrome is hidden via CSS (.art-bottom, .art-progress,
// .art-state). We render our own HUD as an Artplayer `layer`, which keeps it
// inside the player DOM tree so it survives browser fullscreen and doesn't
// fight Artplayer's chrome for z-index.
//
// HUD layout matches design_handoff_animarr/design-system/04-tv.html § T-03
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

    const apiBase = () => (typeof window !== 'undefined' && window.animarrApiBase) || '';
    const apiUrl  = (path) => apiBase() + path;

    // Mixed-content shim for MAUI BlazorWebView (Android).
    //
    // MAUI mounts the Razor bundle at https://0.0.0.x/ (a virtual host that's
    // hardcoded by the framework). If the configured Animarr server lives at
    // plain http://192.168.x.x:port, Chromium's renderer refuses every fetch
    // from the HTTPS page as "active mixed content" — even with
    // WebSettings.MixedContentMode = MIXED_CONTENT_ALWAYS_ALLOW. The block
    // happens before our AnimarrWebViewClient.ShouldInterceptRequest gets a
    // crack at proxying the raw HTTP URL.
    //
    // Workaround: rewrite the api base to a same-origin proxy path of the
    // form `/_animarr_proxy_/<url-encoded-base>`. JS fetches then go to
    // https://0.0.0.x/_animarr_proxy_/... (same-origin, no mixed content);
    // the WebViewClient on Android sees the prefix, decodes the real target,
    // and proxies the call via native HttpClient — which has no
    // mixed-content rules to enforce. End result is a transparent shim that
    // lets HTTP-only LAN servers work from the MAUI HTTPS WebView.
    //
    // Schemes where the helper is a passthrough (no rewrite):
    //   • Plain browser visiting an http://server/  (page already HTTP)
    //   • HTTPS-page + HTTPS-server (Caddy in front)
    // Only the HTTPS-page + HTTP-server combination triggers rewrite.
    window.animarrSetApiBase = function (newBase) {
        if (newBase && typeof newBase === 'string'
            && newBase.toLowerCase().startsWith('http://')
            && typeof window.location !== 'undefined'
            && window.location.protocol === 'https:')
        {
            window.animarrApiBaseRaw = newBase;
            window.animarrApiBase    = '/_animarr_proxy_/' + encodeURIComponent(newBase);
            // eslint-disable-next-line no-console
            console.info('animarr: api base rewritten through same-origin proxy '
                + '(page is HTTPS, server is HTTP) — was', newBase);
        }
        else
        {
            window.animarrApiBase    = newBase || '';
            window.animarrApiBaseRaw = newBase || '';
        }
    };

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
        get currentTime()  { return this.art.currentTime || 0; }
        set currentTime(t) { try { this.art.currentTime = t; } catch {} }
        get duration()     { return this.art.duration || 0; }
        get volume()       { return this.art.volume ?? 1; }
        set volume(v)      { try { this.art.volume = v; } catch {} }
        get muted()        { return !!this.art.muted; }
        set muted(m)       { try { this.art.muted = m; } catch {} }
        get fullscreen()   { return !!document.fullscreenElement; }
        set fullscreen(b)  { try { this.art.fullscreen = b; } catch {} }

        // ── Playback ──────────────────────────────────────────────────
        play()  { try { return this.art.play();  } catch {} }
        pause() { try { this.art.pause(); } catch {} }

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

    /** Availability gate for the future native (ExoPlayer/AVPlayer) adapter.
     *  Phase 2 publishes `window.animarrNativePlayer` from the MAUI host on
     *  platforms where a native player makes sense (Android TV initially).
     *  Phase 1 has no such global, so this always returns false. */
    // eslint-disable-next-line no-unused-vars
    function isNativeAdapterAvailable() {
        const np = (typeof window !== 'undefined') ? window.animarrNativePlayer : null;
        return !!(np && typeof np.isAvailable === 'function' && np.isAvailable());
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
        fwd10:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3.5-7.1"/><polyline points="21 4 21 10 15 10"/><text x="12" y="16" text-anchor="middle" font-size="8" font-weight="700" fill="currentColor" stroke="none" font-family="Inter, system-ui, sans-serif">10</text></svg>',
        audio:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14"/></svg>',
        cc:     '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M9.5 10.5a2 2 0 1 0 0 3"/><path d="M16 10.5a2 2 0 1 0 0 3"/></svg>',
        aspect: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="6" width="18" height="12" rx="1"/><path d="M7 10v4M11 10v4M15 10v4"/></svg>',
        offset: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><line x1="21" y1="6"  x2="14" y2="6"/><line x1="10" y1="6"  x2="3"  y2="6"/><line x1="21" y1="12" x2="12" y2="12"/><line x1="8"  y1="12" x2="3"  y2="12"/><line x1="21" y1="18" x2="16" y2="18"/><line x1="12" y1="18" x2="3"  y2="18"/><circle cx="12" cy="6"  r="2" fill="currentColor"/><circle cx="10" cy="12" r="2" fill="currentColor"/><circle cx="14" cy="18" r="2" fill="currentColor"/></svg>',
        cast:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2 17a3 3 0 0 1 3 3"/><path d="M2 13a7 7 0 0 1 7 7"/><path d="M2 9 a11 11 0 0 1 11 11"/><path d="M2 5h17a2 2 0 0 1 2 2v9"/></svg>',
        volume: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>',
        mute:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><line x1="22" y1="9" x2="16" y2="15"/><line x1="16" y1="9" x2="22" y2="15"/></svg>',
        fsEnter:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9V5a2 2 0 0 1 2-2h4"/><path d="M15 3h4a2 2 0 0 1 2 2v4"/><path d="M21 15v4a2 2 0 0 1-2 2h-4"/><path d="M9 21H5a2 2 0 0 1-2-2v-4"/></svg>',
        fsExit: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3v4a2 2 0 0 1-2 2H3"/><path d="M21 9h-4a2 2 0 0 1-2-2V3"/><path d="M15 21v-4a2 2 0 0 1 2-2h4"/><path d="M3 15h4a2 2 0 0 1 2 2v4"/></svg>',
    };

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

  <div class="vp-hud__bottom">
    <div class="vp-hud__progress">
      <span class="vp-hud__time" data-bind="cur">0:00</span>
      <div class="vp-hud__track" data-act="seek">
        <div class="vp-hud__track-buffer" data-bind="buffer"></div>
        <div class="vp-hud__track-fill"   data-bind="fill"></div>
        <div class="vp-hud__thumb"        data-bind="thumb"></div>
      </div>
      <span class="vp-hud__time" data-bind="dur">0:00</span>
    </div>
    <div class="vp-hud__controls">
      <div class="vp-hud__cluster vp-hud__cluster--l">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="prev"
                aria-label="Previous episode" title="Previous episode (←|◄◄)">
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
      <div class="vp-hud__cluster vp-hud__cluster--r">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="volume"
                aria-label="Volume" title="Volume" data-bind="volume-btn">
          <span class="vp-glyph" data-bind="volume-icon">${I.volume}</span>
          <span class="vp-label">Volume</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="aspect"
                aria-label="Aspect ratio" title="Aspect ratio">
          <span class="vp-glyph">${I.aspect}</span><span class="vp-label">Aspect</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="offset"
                aria-label="Audio sync" title="Audio sync offset"
                data-bind="offset-btn">
          <span class="vp-glyph">${I.offset}</span><span class="vp-label">Sync</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cast"
                aria-label="Cast to TV" title="Send video to TV (DLNA)"
                data-bind="cast-btn">
          <span class="vp-glyph">${I.cast}</span><span class="vp-label">Cast</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="audio"
                aria-label="Audio tracks" title="Audio tracks">
          <span class="vp-glyph">${I.audio}</span><span class="vp-label">Audio</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cc"
                aria-label="Subtitles" title="Subtitles">
          <span class="vp-glyph">${I.cc}</span><span class="vp-label">CC</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="fullscreen"
                aria-label="Fullscreen" title="Fullscreen (F)" data-bind="fs-btn">
          <span class="vp-glyph" data-bind="fs-icon">${I.fsEnter}</span>
          <span class="vp-label" data-bind="fs-label">Fullscreen</span>
        </button>
      </div>
    </div>
  </div>
</div>`.trim();
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

    // ── audio-offset slider popup ─────────────────────────────────────
    function openOffsetPopup(root, anchor, channel) {
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
        const label = channel === 'sw'
            ? 'HDR / 10-bit (stream-copy)'
            : 'Standard (re-encode)';

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
        };

        // ── auto-hide ─────────────────────────────────────────────────
        const HIDE_MS = 3500;
        let hideTimer = null;
        let hovering = false;
        let dragging = false;
        function show() {
            hud.classList.remove('vp-hud--hidden');
            hud.setAttribute('data-visible', 'true');
            root.style.cursor = '';
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
            if (!adapter.playing || hovering || dragging) return;
            hideTimer = setTimeout(() => {
                if (hovering || dragging || !adapter.playing) return;
                if (root.querySelector('.vp-hud-popup')) return;  // popup open
                hud.classList.add('vp-hud--hidden');
                hud.setAttribute('data-visible', 'false');
                root.style.cursor = 'none';
            }, HIDE_MS);
        }
        hud.addEventListener('mouseenter', () => { hovering = true;  show(); });
        hud.addEventListener('mouseleave', () => { hovering = false; show(); });
        const onActivity = () => show();
        root.addEventListener('mousemove',  onActivity);
        root.addEventListener('pointermove', onActivity);
        root.addEventListener('touchstart', onActivity, { passive: true });

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
                    adapter.fullscreen = !adapter.fullscreen;
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
                case 'fwd10':  seekBy(+10); break;
                case 'aspect': callbacks.aspect(); break;
                case 'offset': callbacks.offset(); break;
                case 'cast':   callbacks.cast(); break;
                case 'volume': callbacks.volume(); break;
                case 'audio':  callbacks.audio(); break;
                case 'cc':     callbacks.cc(); break;
                case 'fullscreen':
                    adapter.fullscreen = !adapter.fullscreen;
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
        refs.track.addEventListener('pointerdown', (e) => {
            dragging = true;
            try { refs.track.setPointerCapture(e.pointerId); } catch {}
            seekToPct(pctFromEvent(e));
            show();
        });
        refs.track.addEventListener('pointermove', (e) => {
            if (!dragging) return;
            seekToPct(pctFromEvent(e));
            show();
        });
        const endDrag = (e) => {
            if (!dragging) return;
            dragging = false;
            try { refs.track.releasePointerCapture(e.pointerId); } catch {}
            show();
        };
        refs.track.addEventListener('pointerup',     endDrag);
        refs.track.addEventListener('pointercancel', endDrag);

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
            refs.playIcon.innerHTML    = I.pause;
            refs.playLabel.textContent = 'Pause';
            show();
        });
        adapter.on('pause', () => {
            refs.playIcon.innerHTML    = I.play;
            refs.playLabel.textContent = 'Play';
            // Paused → keep HUD visible.
            hud.classList.remove('vp-hud--hidden');
            hud.setAttribute('data-visible', 'true');
            root.style.cursor = '';
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
            if (hideTimer) clearTimeout(hideTimer);
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
        const forceResumeSec  = (opts && Number.isFinite(opts.forceResumeSec))
            ? Math.max(0, opts.forceResumeSec)  : null;
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
        const startUrl = apiUrl('/api/hls/start?path=' + encodeURIComponent(mediaPath)
            + (resumeSec > 0 ? '&seek=' + resumeSec.toFixed(2) : '')
            + '&audioOffsetHwMs=' + audioOffsetMsHw
            + '&audioOffsetSwMs=' + audioOffsetMsSw
            + (audioTrackIndex > 0 ? '&audioTrackIndex=' + audioTrackIndex : ''));
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
        if (!manifestUrl && !directPlayUrl) return;
        if (abort.signal.aborted) return;

        // ── 4) Probe ──────────────────────────────────────────────────
        let subtitleList = [];
        let audioList = [];
        let mediaInfo = null;
        if (mediaPath) {
            try {
                const probeRes = await fetch(apiUrl('/api/probe?path=' + encodeURIComponent(mediaPath)),
                    { signal: abort.signal });
                if (abort.signal.aborted) return;
                if (probeRes.ok) {
                    const data = await probeRes.json();
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
                            playbackTier:  directPlayUrl ? 'directplay' : 'hls',
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

        const offsetChannel = determineOffsetChannel(entry.output, mediaInfo);

        // ── 5) Instantiate player + adapter ───────────────────────────
        const playUrl = directPlayUrl
            ? (directPlayUrl.startsWith('/') ? apiUrl(directPlayUrl) : directPlayUrl)
            : (manifestUrl.startsWith('/')   ? apiUrl(manifestUrl)   : manifestUrl);
        const isHls   = !directPlayUrl;
        const fileExt = mediaPath.toLowerCase().split('.').pop();
        const stylePref = readStylePref();

        let art = null;
        let adapter;

        // Codec capability gate: ask Android's MediaCodecList whether the
        // device can decode whatever the server's output describes. If not,
        // fall through to Artplayer (which might still fail, but at least
        // gives us softare decode fallback through the WebView). Skipped
        // entirely on hosts where the native bridge isn't published.
        let nativeAllowed = isNativeAdapterAvailable() && !opts?.forceWebPlayer;
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
            art = new window.Artplayer({
            container: el,
            url: playUrl,
            type: isHls ? 'm3u8' : (fileExt || 'mp4'),
            customType: isHls ? {
                m3u8: (video, url) => {
                    if (!window.Hls || !window.Hls.isSupported()) {
                        video.src = url;
                        return;
                    }
                    const hls = new window.Hls({
                        fragLoadingTimeOut:     60000,
                        fragLoadingMaxRetry:    8,
                        manifestLoadingTimeOut: 30000,
                        levelLoadingTimeOut:    30000,
                        maxBufferHole:          4,
                        maxFragLookUpTolerance: 2,
                        nudgeOffset:            0.5,
                        nudgeMaxRetry:          10,
                        maxBufferLength:        60,
                        maxMaxBufferLength:     600,
                    });
                    hls.loadSource(url);
                    hls.attachMedia(video);
                    art.hls = hls;
                    art.on('destroy', () => { try { hls.destroy(); } catch {} });
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
            theme: 'oklch(0.72 0.18 245)',
            lang: 'en',
            moreVideoAttr: { crossorigin: 'anonymous' },
            subtitle: subtitleList.length > 0 ? {
                url: (subtitleList.find(s => s.default) || subtitleList[0]).url,
                // Artplayer accepts 'vtt' | 'srt' | 'ass'. 'webvtt' is NOT
                // a recognised value and silently dropped on the loader path
                // — fix landed 2026-05-27 after subtitle.switch() reported
                // no-op effect on every track change.
                type: 'vtt',
                encoding: 'utf-8',
                escape: false,
            } : undefined,
            layers: [{
                name: 'animarr-hud',
                html: buildHudHtml(stylePref),
                style: { position: 'absolute', inset: '0', zIndex: '30',
                         pointerEvents: 'none' },
            }],
            });
            adapter = new ArtplayerAdapter(art);
        }

        entry.art = art;
        entry.adapter = adapter;
        entry.currentSubIdx = subtitleList.findIndex(s => s.default);
        if (entry.currentSubIdx < 0 && subtitleList.length > 0) entry.currentSubIdx = 0;

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
                openOffsetPopup(hudRoot, 'offset', offsetChannel);
            },
            audio: () => {
                if (entry.audioList.length === 0) return;
                openPickerPopup(hudRoot, 'audio', 'Audio tracks',
                    entry.audioList.map(a => a.label),
                    entry.currentAudIdx,
                    (i) => {
                        if (i === entry.currentAudIdx) return;
                        // HLS sessions transcode a single audio stream, so live
                        // switching via hls.js / video.audioTracks is a no-op.
                        // We tear down the session and start a fresh one with
                        // `-map 0:a:{i}?` baked into ffmpeg, carrying current
                        // position over as the resume seek.
                        switchAudioTrack(elementId, i);
                    });
            },
            cc: () => {
                const items = ['Off', ...entry.subtitleList.map(s => s.name)];
                const cur = entry.currentSubIdx == null ? 0 : entry.currentSubIdx + 1;
                openPickerPopup(hudRoot, 'cc', 'Subtitles', items, cur, (i) => {
                    if (i === 0) {
                        adapter.setSubtitle(null);
                        entry.currentSubIdx = null;
                    } else {
                        const s = entry.subtitleList[i - 1];
                        adapter.setSubtitle({ url: s.url, name: s.name, type: 'vtt' });
                        entry.currentSubIdx = i - 1;
                    }
                });
            },
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
            const now = new Date();
            const hh = String(now.getHours()).padStart(2, '0');
            const mm = String(now.getMinutes()).padStart(2, '0');
            const bits = [`${hh}:${mm}`];
            const o = entry.output;
            if (o) {
                if (o.height) bits.push(`${o.height}p`);
                if (o.videoCodec) bits.push(o.videoCodec.toUpperCase());
                if (o.bitDepth >= 10) bits.push('10-bit');
                (o.hdrFormats || []).forEach(fmt => {
                    bits.push(fmt === 'dolbyvision' ? 'DV'
                            : fmt === 'hdr10' ? 'HDR10'
                            : fmt === 'hlg' ? 'HLG' : fmt.toUpperCase());
                });
                if (o.audioCodec) {
                    const ch = o.audioChannels || 0;
                    const chLabel = ch === 1 ? 'Mono' : ch === 2 ? 'Stereo'
                                  : ch === 6 ? '5.1' : ch === 8 ? '7.1'
                                  : ch > 0 ? `${ch}ch` : '';
                    bits.push(o.audioCodec.toUpperCase()
                        + (chLabel ? ' ' + chLabel : ''));
                }
                if (o.audioLanguage) bits.push(o.audioLanguage.toUpperCase());
                // Playback path tag — what the server actually did with the
                // source. "Direct" is best, the rest indicate transcoding.
                const planTag = {
                    'directplay':     'Direct',
                    'ts-copy':        'TS-copy',
                    'vaapi-reencode': 'VAAPI→H.264',
                    'nvenc-reencode': 'NVENC→H.264',
                    'fmp4-copy':      'fMP4-copy',
                }[o.plan] || 'HLS';
                bits.push(planTag);
            }
            hudCtl.setMeta(bits.join(' · '));
            // Hover tooltip with the server-supplied transcode reason — only
            // when we actually transcoded. For Direct Play / unknown the
            // attribute is cleared so no stale tooltip lingers.
            const root2 = art?.template?.$player || art?.container;
            const metaEl = root2 && root2.querySelector('[data-bind="meta"]');
            if (metaEl) {
                metaEl.title = (o && o.transcoded && o.transcodeReason) ? o.transcodeReason : '';
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
        if (resumeSec > 0) {
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
    async function switchAudioTrack(elementId, audioTrackIndex) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter) return;
        const pos       = entry.adapter.currentTime || 0;
        const dotnetRef = entry.dotnetRef;
        const mediaPath = entry.mediaPath;
        if (!dotnetRef || !mediaPath) return;
        // Tear down the old session synchronously — detach() handles HLS
        // DELETE + Artplayer destroy + WIRED cleanup.
        detach(elementId);
        // Brief delay so the server has a tick to clean up the old ffmpeg
        // process / tmp dir before we ask for a new one (avoids racing the
        // dedup-by-source-path step inside StartAsync).
        await new Promise(r => setTimeout(r, 100));
        await attach(elementId, dotnetRef, mediaPath, {
            audioTrackIndex,
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
        setStyle, switchAudioTrack,
    };
})();
