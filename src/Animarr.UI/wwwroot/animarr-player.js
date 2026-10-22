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
    // elementId → what must outlive a stream restart on the same player element
    // (audio / quality switch, a fallback rung). A restart tears the WIRED
    // entry down and builds a fresh one, so anything that lived only there —
    // all of setMediaSession's data: HUD title, skip intro/credits segments,
    // up-next + autoplay, trickplay previews, lock-screen metadata, prev/next —
    // was silently lost on every switch. Only the public detach() (player
    // closed, or Blazor moving on to another episode) clears it.
    //   session   — { meta, handlers } as last passed to setMediaSession
    //   watchNext — setWatchNextMeta's payload
    const KEEP = new Map();
    function keepFor(elementId) {
        let k = KEEP.get(elementId);
        if (!k) { k = {}; KEEP.set(elementId, k); }
        return k;
    }

    // ── Vendor libraries, loaded on demand ──────────────────────────────────
    // hls.js + Artplayer used to be parser-blocking <head> scripts in both
    // index.html hosts — every page load (and every phone launch) waited on
    // the CDN and compiled ~1 MB of JS before the boot splash could paint,
    // though only the player needs them. Now attach() loads them on first use
    // and MediaDetail warms them via preload() once a title page is open, so
    // pressing Play doesn't wait. Same pinned versions + SRI as before.
    // artplayer pinned to 5.4.0: the floating `artplayer@5` tag shipped a newer
    // 5.x that tightened option validation (option.subtitle must be an object)
    // and broke playback on media without subtitles.
    const PLAYER_LIBS = [
        ['https://cdn.jsdelivr.net/npm/hls.js@1.5.20/dist/hls.min.js',
         'sha384-V5ruNBgmYcC3SJRUQeNykAAAgde5gOFq/Hu0CZj7bygDP0yRIhkvX8+w0u/7mRvr'],
        ['https://cdn.jsdelivr.net/npm/artplayer@5.4.0/dist/artplayer.js',
         'sha384-w5IjjTXa5FVlGgCubBLyhTG2jrrNp7ktdnxNNUdVZuYwpQ5OqIab8c1gjKDD3+uO'],
    ];
    const SCRIPT_LOADS = new Map();   // src → Promise; a failed load is forgotten so the next call retries
    function loadScriptOnce(src, integrity) {
        let p = SCRIPT_LOADS.get(src);
        if (p) return p;
        p = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src;
            s.integrity = integrity;
            s.crossOrigin = 'anonymous';
            s.onload = () => resolve();
            s.onerror = () => {
                SCRIPT_LOADS.delete(src);
                s.remove();
                reject(new Error('animarrPlayer: failed to load ' + src));
            };
            document.head.appendChild(s);
        });
        SCRIPT_LOADS.set(src, p);
        return p;
    }
    function loadPlayerLibs() {
        if (typeof window.Artplayer === 'function' && window.Hls) return Promise.resolve();
        return Promise.all(PLAYER_LIBS.map(([src, sri]) => loadScriptOnce(src, sri)));
    }
    // Fire-and-forget warm-up (MediaDetail calls it once the page has loaded):
    // the vendor libraries and the client-capability probe, so Play waits on
    // neither.
    function preload() {
        loadPlayerLibs().catch(() => { /* attach() retries and reports */ });
        clientCaps();
    }

    // ── User audio/subtitle preferences ─────────────────────────────────────
    // Pushed once from MediaDetail via animarrPlayer.setPrefs(...) when the
    // account prefs load. audioLang/subLang are ISO-639-1 codes ('ja','ru') or
    // null; subSize is a pixel size or 0 (= Artplayer's default 20 px).
    //   • subMode  → which subtitle a fresh open starts with (Personal
    //                settings → Subtitles; same four modes as the TV):
    //                  'default' — what the file flags: its default track,
    //                              else a forced / signs track in the audio's
    //                              language, else none;
    //                  'always'  — a track in the preferred language, else
    //                              the flagged default, else the first one
    //                              ('auto' is the old name);
    //                  'forced'  — only a forced / signs track, else none;
    //                  'off'     — none.
    //   • subLang  → the preferred subtitle language for 'always' (client-side
    //                overlay only — never touches the video/audio pipeline).
    //   • subSize  → base cue font size; the player's own size step scales it.
    //   • audioLang→ auto-selects the matching audio track ONLY on a transcoding
    //                HLS session (see attach). Direct Play is never disturbed:
    //                we never force a transcode just to switch audio language.
    //   • normalizeVolume → level every audio track / dub to the same loudness
    //                (EBU R128 from /api/loudness, applied by a WebAudio gain +
    //                limiter — setupLoudness);
    //   • defaultVolume → the volume a device starts at before anything was
    //                set on it (0–100; the player's own slider then wins).
    let PREFS = { audioLang: null, subLang: null, subSize: 0, subMode: 'always', normalizeVolume: false, defaultVolume: null };
    const SUB_MODES = ['default', 'always', 'forced', 'off'];
    function setPrefs(p) {
        let mode = (p && p.subMode) ? String(p.subMode).toLowerCase() : 'always';
        if (mode === 'auto') mode = 'always';
        PREFS = {
            audioLang: (p && p.audioLang) ? String(p.audioLang).toLowerCase() : null,
            subLang:   (p && p.subLang)   ? String(p.subLang).toLowerCase()   : null,
            subSize:   (p && Number.isFinite(p.subSize)) ? Math.max(0, p.subSize) : 0,
            subMode:   SUB_MODES.includes(mode) ? mode : 'always',
            normalizeVolume: !!(p && p.normalizeVolume),
            defaultVolume:   (p && Number.isFinite(p.defaultVolume)) ? Math.max(0, Math.min(100, p.defaultVolume)) : null,
        };
    }
    // A sidecar file that is a forced / signs track (no disposition flags on
    // a loose file — its name is all there is).
    const FORCED_NAME_RE = /\bforced\b|\bsigns?\b|надпис|написи/i;
    /** Initial subtitle for a fresh open, per the account's subtitle mode
     *  (PREFS.subMode, see above):
     *    { idx }  — an embedded track (index into `subs`, the probed list),
     *    { ext }  — a sidecar file (its path, from `ext`),
     *    null     — none.
     *  `audioLang` is the playing audio's language (639-1 or ''), which picks
     *  the forced / signs track a foreign-language scene needs. */
    function pickDefaultSubtitle(subs, ext, audioLang) {
        subs = subs || []; ext = ext || [];
        const mode = PREFS.subMode;
        if (mode === 'off') return null;
        const emb = (pred) => { const i = subs.findIndex(pred); return i >= 0 ? { idx: i } : null; };
        const side = (pred) => { const s = ext.find(pred); return s ? { ext: s.path } : null; };
        const lang = (s) => normLang(s.lang);
        const extLang = (s) => normLang(s.language);
        const extForced = (s) => FORCED_NAME_RE.test((s.fileName || '') + ' ' + (s.label || ''));
        if (mode === 'forced') {
            return (PREFS.subLang && emb(s => s.forced && lang(s) === PREFS.subLang))
                || (audioLang && emb(s => s.forced && lang(s) === audioLang))
                || emb(s => s.forced)
                || side(extForced);
        }
        if (mode === 'default') {
            return (PREFS.subLang && emb(s => s.default && lang(s) === PREFS.subLang))
                || emb(s => s.default)
                || (audioLang && emb(s => s.forced && lang(s) === audioLang))
                || null;
        }
        // always
        if (PREFS.subLang) {
            const hit = emb(s => !s.forced && lang(s) === PREFS.subLang)
                || side(s => !extForced(s) && extLang(s) === PREFS.subLang)
                || emb(s => lang(s) === PREFS.subLang)
                || side(s => extLang(s) === PREFS.subLang);
            if (hit) return hit;
        }
        return emb(s => s.default) || emb(s => !s.forced) || emb(() => true)
            || side(s => !extForced(s)) || side(() => true);
    }
    // Normalise any track language tag (ISO-639-2/B or /T, ISO-639-1, or a full
    // English name) to a 639-1 2-letter code so preference matching is tolerant
    // of how a muxer tagged the stream. Scope mirrors LanguageNameMap, plus
    // Ukrainian (a UI language with its own dubs).
    const LANG_2TO1 = {
        jpn:'ja', ja:'ja', eng:'en', en:'en', rus:'ru', ru:'ru', ukr:'uk', uk:'uk',
        ger:'de', deu:'de', de:'de', fre:'fr', fra:'fr', fr:'fr',
        spa:'es', es:'es', ita:'it', it:'it', por:'pt', pt:'pt',
        chi:'zh', zho:'zh', cmn:'zh', yue:'zh', zh:'zh', cn:'zh',
        kor:'ko', ko:'ko', tha:'th', th:'th', vie:'vi', vi:'vi',
        ind:'id', id:'id', ara:'ar', ar:'ar', hin:'hi', hi:'hi', tur:'tr', tr:'tr',
        japanese:'ja', english:'en', russian:'ru', ukrainian:'uk', german:'de', french:'fr',
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

    // ── Track labels (task 2.10) ──────────────────────────────────────
    // "Japanese · AC-3 5.1 · default", "Russian · AniLibria · MKA · external":
    // the full language name in the UI language (Intl.DisplayNames; MediaDetail
    // passes the UI language as opts.uiLang), the track title when it says
    // more than the language, codec, channel layout and the default / forced
    // / commentary flags.
    let UI_LANG = 'en';
    const DISPLAY_NAMES = new Map();   // locale → Intl.DisplayNames | null
    function displayNames(locale) {
        if (!DISPLAY_NAMES.has(locale)) {
            let dn = null;
            try { dn = new Intl.DisplayNames([locale], { type: 'language', fallback: 'none' }); } catch {}
            DISPLAY_NAMES.set(locale, dn);
        }
        return DISPLAY_NAMES.get(locale);
    }
    /** Full language name for a track tag ("jpn", "ja", "japanese") in
     *  `locale` (default: the UI language); '' for none / und; the raw tag
     *  upper-cased when nothing knows it. */
    function languageName(tag, locale) {
        const raw = String(tag || '').trim();
        if (!raw || raw.toLowerCase() === 'und') return '';
        const loc = locale || UI_LANG;
        const code = normLang(raw) || raw.toLowerCase();
        const dn = displayNames(loc);
        let name = null;
        try { name = dn ? dn.of(code) : null; } catch { /* not a language tag */ }
        if (!name) return raw.toUpperCase();
        return name.charAt(0).toLocaleUpperCase(loc) + name.slice(1);
    }
    const CODEC_NAMES = {
        aac: 'AAC', ac3: 'AC-3', eac3: 'E-AC-3', dts: 'DTS', truehd: 'TrueHD', mlp: 'MLP',
        flac: 'FLAC', alac: 'ALAC', opus: 'Opus', vorbis: 'Vorbis', mp3: 'MP3', mp2: 'MP2',
        subrip: 'SRT', srt: 'SRT', ass: 'ASS', ssa: 'SSA', webvtt: 'WebVTT', vtt: 'WebVTT',
        mov_text: 'TX3G', hdmv_pgs_subtitle: 'PGS', pgssub: 'PGS', sup: 'PGS',
        dvd_subtitle: 'VobSub', dvdsub: 'VobSub', dvb_subtitle: 'DVB', dvb_teletext: 'Teletext',
        eia_608: 'CC 608',
    };
    /** "AC-3", "DTS-HD MA", "TrueHD Atmos"… from ffprobe's codec_name (+ profile). */
    function codecName(codec, profile) {
        const c = String(codec || '').toLowerCase();
        if (!c) return '';
        const p = String(profile || '');
        if (c.startsWith('pcm_')) return 'PCM';
        if (c === 'dts') {
            if (/HD MA/i.test(p)) return 'DTS-HD MA';
            if (/HD HRA|HD HR/i.test(p)) return 'DTS-HD HRA';
            if (/DTS:X|DTS-X/i.test(p)) return 'DTS:X';
        }
        const base = CODEC_NAMES[c] || c.toUpperCase();
        return /atmos/i.test(p) ? base + ' Atmos' : base;
    }
    /** "2.0", "5.1", "7.1"… */
    function channelLabel(ch) {
        ch = ch | 0;
        if (ch <= 0) return '';
        return ({ 1: '1.0', 2: '2.0', 3: '2.1', 6: '5.1', 7: '6.1', 8: '7.1' })[ch] || ch + 'ch';
    }
    /** Is a track title only a restatement of its language (or of a flag
     *  shown anyway)? "English", "eng", "Русский", "Forced" → drop it. */
    function titleIsRedundant(title, langTag, flagged) {
        const tl = String(title || '').trim().toLowerCase();
        if (!tl) return true;
        if (langTag) {
            const names = [langTag, normLang(langTag), languageName(langTag), languageName(langTag, 'en')];
            if (names.some(n => n && n.toLowerCase() === tl)) return true;
        }
        return (flagged.forced && tl === 'forced') || (flagged.default && tl === 'default');
    }
    function trackFlagWords(f) {
        const out = [];
        if (f.default)    out.push(t('track_default'));
        if (f.forced)     out.push(t('track_forced'));
        if (f.commentary) out.push(t('track_commentary'));
        return out;
    }
    /** Label for an embedded audio / subtitle stream (ffprobe stream). */
    function embeddedTrackLabel(s, idx, forcedOverride) {
        const tags = s.tags || {};
        const lang  = tags.language || tags.LANGUAGE || '';
        const title = tags.title || tags.TITLE || '';
        const d = s.disposition || {};
        const flags = {
            default:    !!d.default,
            forced:     forcedOverride != null ? !!forcedOverride : !!d.forced,
            commentary: !!d.comment || /\bcommentary\b|комментар/i.test(title),
        };
        const parts = [];
        const ln = languageName(lang);
        if (ln) parts.push(ln);
        if (!titleIsRedundant(title, lang, flags)) parts.push(title.trim());
        const codec = codecName(s.codec_name, s.profile);
        const ch = s.codec_type === 'audio' ? channelLabel(s.channels) : '';
        const tech = [codec, ch].filter(Boolean).join(' ');
        if (!parts.length) parts.push(tf('track_n', idx + 1));
        if (tech) parts.push(tech);
        return parts.concat(trackFlagWords(flags)).join(' · ');
    }
    /** The part of a sidecar file's server label that isn't its language
     *  (the server prepends "Russian · " or answers just "Russian" / its
     *  "External audio" fallback). */
    function externalLabelCore(x) {
        let label = String(x.label || '').trim();
        const en = x.language ? languageName(x.language, 'en') : '';
        if (en && label.toLowerCase().startsWith(en.toLowerCase() + ' · ')) label = label.slice(en.length + 3).trim();
        // A folder named after the language ("Russian Dub") → "Dub".
        else if (en && label.toLowerCase().startsWith(en.toLowerCase() + ' ')) label = label.slice(en.length + 1).trim();
        if (/^External (audio|subtitle)$/i.test(label)) return '';
        return titleIsRedundant(label, x.language, {}) ? '' : label;
    }
    /** Label for a sidecar dub / subtitle file (ExternalTrackDto). Uses codec
     *  / channels when the server reports them (plan 2.9), else the file
     *  extension. */
    function externalTrackLabel(x) {
        const parts = [];
        const ln = languageName(x.language);
        if (ln) parts.push(ln);
        const core = externalLabelCore(x);
        if (core) parts.push(core);
        if (!parts.length) parts.push(x.fileName || x.label || '?');
        const tech = [x.codec ? codecName(x.codec) : String(x.ext || '').toUpperCase(),
                      channelLabel(x.channels)].filter(Boolean).join(' ');
        if (tech) parts.push(tech);
        if (FORCED_NAME_RE.test((x.fileName || '') + ' ' + (x.label || ''))) parts.push(t('track_forced'));
        parts.push(t('track_external'));
        return parts.join(' · ');
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
    // Signed media token (?mt=). The media endpoints require authorization;
    // a same-origin browser sends its cookie, but the MAUI WebView talks to the
    // server cross-origin (via the loopback proxy or directly) where the
    // SameSite cookie never rides along. Blazor publishes the user's session
    // token through animarrSetMediaToken only in that case — unset (plain
    // browser) means "cookie only" and URLs stay untouched. Evaluated per call
    // so a token refresh applies to the next request.
    const withMediaToken = (url) => {
        const t = (typeof window !== 'undefined') && window.animarrMediaToken;
        if (!t || !url || /[?&]mt=/.test(url)) return url;
        return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'mt=' + encodeURIComponent(t);
    };
    const apiUrl  = (path) => withMediaToken(apiBase() + path);
    window.animarrSetMediaToken = function (token) {
        window.animarrMediaToken = token || '';
    };

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
    // ── Subtitle cue rendering (safe) ─────────────────────────────────
    // Artplayer paints the active cues with innerHTML; with `escape:false` the
    // raw text of a subtitle file (often shipped inside a torrent) became live
    // HTML in the page — `<img src=x onerror=…>` in an .srt ran script. The
    // player now runs Artplayer with escape:true (inert text as the fallback)
    // and repaints each cue change through this renderer, which builds DOM
    // nodes itself: text only ever lands in text nodes, and the few tags worth
    // keeping become real elements — <b> <i> <u>, WebVTT colour classes
    // (<c.yellow>) and the SRT-style <font color="…">. Every other tag
    // (<v>, <lang>, <ruby>, timestamps, anything unknown) is dropped and only
    // its text kept.
    const CUE_TAGS = { b: 'b', i: 'i', u: 'u' };
    // WebVTT §"default text colour" classes (`<c.yellow>`).
    const CUE_COLORS = {
        white: '#fff', lime: '#0f0', cyan: '#0ff', red: '#f00',
        yellow: '#ff0', magenta: '#f0f', blue: '#00f', black: '#000',
    };
    const CUE_ENTITIES = {
        amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ',
        lrm: '‎', rlm: '‏',
    };
    function decodeCueEntities(s) {
        return s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
            if (e[0] === '#') {
                const cp = (e[1] === 'x' || e[1] === 'X') ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
                return (Number.isFinite(cp) && cp > 0 && cp <= 0x10ffff) ? String.fromCodePoint(cp) : m;
            }
            const v = CUE_ENTITIES[e.toLowerCase()];
            return v != null ? v : m;
        });
    }
    // A colour value we are willing to put into style.color: #rgb[a] /
    // #rrggbb[aa] or a bare CSS colour keyword. Assigned through the CSSOM
    // (never as markup), so even a junk value can't escape the property.
    function safeCueColor(v) {
        v = String(v || '').trim().toLowerCase();
        return /^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/.test(v) || /^[a-z]{3,20}$/.test(v) ? v : null;
    }
    /** Append one cue line (raw WebVTT/SRT cue text) to `parent` as DOM. */
    function appendCueLine(parent, line) {
        const stack = [{ tag: null, node: parent }];
        const top = () => stack[stack.length - 1].node;
        const text = (s) => { if (s) top().appendChild(document.createTextNode(decodeCueEntities(s))); };
        let i = 0;
        while (i < line.length) {
            const lt = line.indexOf('<', i);
            if (lt < 0) { text(line.slice(i)); break; }
            text(line.slice(i, lt));
            const gt = line.indexOf('>', lt + 1);
            if (gt < 0) { text(line.slice(lt)); break; }   // stray '<' → literal text
            const raw = line.slice(lt + 1, gt);
            i = gt + 1;
            const closing = raw[0] === '/';
            const m = /^([a-z][a-z0-9]*)((?:\.[\w-]+)*)/i.exec(closing ? raw.slice(1) : raw);
            if (!m) {
                // <00:01.000> karaoke timestamps and <!…> are markup → drop;
                // anything else ("5 < 6 > 4") was never a tag → keep as text.
                if (!/^\s*[\d!]/.test(raw)) text(line.slice(lt, gt + 1));
                continue;
            }
            const name = m[1].toLowerCase();
            if (closing) {
                // Close the innermost matching element (ignore unmatched ones).
                for (let k = stack.length - 1; k > 0; k--) {
                    if (stack[k].tag === name) { stack.length = k; break; }
                }
                continue;
            }
            let el = null;
            const classes = m[2] ? m[2].slice(1).toLowerCase().split('.') : [];
            const classColor = classes.map(c => CUE_COLORS[c]).find(Boolean) || null;
            if (CUE_TAGS[name]) {
                el = document.createElement(CUE_TAGS[name]);
            } else if (name === 'c' || name === 'font') {
                el = document.createElement('span');
                if (name === 'font') {
                    const cm = /\bcolor\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))/i.exec(raw);
                    const col = cm ? safeCueColor(cm[1] ?? cm[2] ?? cm[3]) : null;
                    if (col) el.style.color = col;
                }
            }
            if (!el) continue;                              // unsupported tag: keep its text only
            if (classColor) el.style.color = classColor;
            top().appendChild(el);
            stack.push({ tag: name, node: el });
        }
    }
    /** Repaint Artplayer's subtitle box from the active cues (same structure
     *  Artplayer produces: one .art-subtitle-line per non-empty cue line). */
    function renderCuesSafely($subtitle, cues) {
        if (!$subtitle) return;
        $subtitle.textContent = '';
        (cues || []).forEach((cue, group) => {
            String((cue && cue.text) || '').split(/\r?\n/).filter(l => l.trim()).forEach(line => {
                const div = document.createElement('div');
                div.className = 'art-subtitle-line';
                div.dataset.group = String(group);
                appendCueLine(div, line);
                $subtitle.appendChild(div);
            });
        });
    }

    // ── Subtitle timing offset (task 2.7) ─────────────────────────────
    // THE one setter: the timing panel, a restart carrying the value over and
    // a newly loaded track all go through setSubtitleOffset /
    // applySubtitleOffset, so when per-title track prefs land on the server
    // (plan §6.5, PUT /api/me/track-prefs + `subtitleOffset` in the start
    // response) this is the single place that saves / seeds it. Seconds,
    // positive = cues appear later. Lives for this player session; it applies
    // to whichever track is loaded (Artplayer keeps the shift per <track>, so
    // a newly loaded one starts at 0 until this re-applies it).
    const SUB_OFFSET_MAX = 10;   // same range as Artplayer's own offset control
    /** `save`: the user changed it — remember it for the title (track memory,
     *  task 2.2: PUT subtitleOffsetMs, debounced). A restart / the title's
     *  remembered value coming back in don't save. */
    function setSubtitleOffset(entry, sec, save) {
        let v = Math.round((Number(sec) || 0) * 10) / 10;
        v = Math.max(-SUB_OFFSET_MAX, Math.min(SUB_OFFSET_MAX, v));
        entry.subOffset = v;
        applySubtitleOffset(entry);
        // 'reset': forget it for the title (an explicit 0 would be kept as 0).
        if (save && entry.elementId)
            queueTrackPrefs(entry.elementId, save === 'reset' ? { clear: ['subtitleOffset'] } : { subtitleOffsetMs: Math.round(v * 1000) });
        return v;
    }
    // Shifts the loaded track's cues the way Artplayer's own subtitleOffset
    // does (from each cue's original times, so re-applying is idempotent),
    // minus its clamp to [0, art.duration]: that duration reads 0 until the
    // stream's metadata is in (a track loading during a restart) and for
    // ever on a Direct Stream remux (video.duration = Infinity), which
    // collapsed every cue to 0 s. A cue shifted past the end just never shows.
    function applySubtitleOffset(entry) {
        const art = entry && entry.art;
        if (!art || !art.subtitle) return;
        const want = entry.subOffset || 0;
        try {
            for (const c of (art.subtitle.cues || [])) {
                if (c.vpOrigStart == null) {
                    if (want === 0) continue;
                    c.vpOrigStart = c.startTime;
                    c.vpOrigEnd   = c.endTime;
                }
                c.startTime = Math.max(0, c.vpOrigStart + want);
                c.endTime   = Math.max(0, c.vpOrigEnd + want);
            }
            art.subtitle.update();
        } catch (e) { console.warn('subtitle offset failed', e); }
        const r = entry.subRenderer;
        if (r && r.kind === 'ass') {
            try { r.inst.timeOffset = -want; r.inst.resize(true); } catch {}
        } else if (r && r.kind === 'bitmap') {
            try { r.inst.timeOffset = -want; } catch {}
        }
    }

    // ── Subtitle renderers (tasks 2.5 / 2.6) ──────────────────────────
    // A subtitle choice renders one of these ways (subtitlePlan):
    //   text — WebVTT through Artplayer's <track> + renderCuesSafely (SRT,
    //          VTT, mov_text, and an ASS script whose styling a WebVTT
    //          rendering doesn't lose — /api/subtitle/tracks says so);
    //   ass  — the ORIGINAL ASS / SSA script through JASSUB (libass in
    //          WebAssembly: positions, karaoke, typesetting, the file's own
    //          fonts), for every ASS track the server marks complex or hasn't
    //          analysed yet, and every sidecar .ass / .ssa;
    //   bitmap — PGS (embedded via /api/subtitle/sup, or a sidecar .sup) and
    //          sidecar VobSub (.idx + .sub) through libbitsub (Rust/WASM,
    //          vendor/libbitsub, MIT);
    //   burn  — what nothing here can draw (embedded VobSub / DVB, or no
    //          bitmap renderer on this device): the server burns track s:N
    //          into the picture (burnSubtitle=N, a re-encode) — only on the
    //          user's explicit pick, also offered next to every embedded
    //          bitmap track in the CC menu.
    // JASSUB is self-hosted under wwwroot/vendor/jassub (the server may be
    // offline / LAN-only) and loaded on first use; the fonts attached to the
    // video come from /api/subtitle/tracks, with the bundled Liberation Sans
    // as the fallback. One renderer per player: replaced on every choice,
    // disposed on restart / close (teardown).
    const PLAYER_BASE = (() => {
        try { if (document.currentScript && document.currentScript.src) return new URL('.', document.currentScript.src).href; } catch {}
        return new URL('_content/Animarr.UI/', document.baseURI).href;
    })();
    const vendorUrl = (p) => new URL('vendor/' + p, PLAYER_BASE).href;
    /** A media URL the server answered relative ("/api/subtitle?…"). */
    const mediaApiUrl = (rel) => !rel ? null : /^https?:/i.test(rel) ? withMediaToken(rel) : apiUrl(rel);
    let JASSUB_LOAD = null;
    function loadJassub() {
        if (!JASSUB_LOAD) {
            JASSUB_LOAD = import(vendorUrl('jassub/jassub.js')).then(m => m.default)
                .catch((e) => { JASSUB_LOAD = null; throw e; });
        }
        return JASSUB_LOAD;
    }
    let BITSUB_LOAD = null, BITSUB_BROKEN = false;
    function loadBitsub() {
        if (!BITSUB_LOAD) {
            BITSUB_LOAD = import(vendorUrl('libbitsub/libbitsub.bundle.js'))
                .catch((e) => { BITSUB_LOAD = null; BITSUB_BROKEN = true; throw e; });
        }
        return BITSUB_LOAD;
    }
    /** Can this device draw bitmap subtitles itself (else: burn-in)? */
    function canDrawBitmaps() {
        return !BITSUB_BROKEN && typeof WebAssembly === 'object' && typeof Worker === 'function';
    }
    const IMAGE_SUB_CODECS = { hdmv_pgs_subtitle: 'pgs', pgssub: 'pgs', dvd_subtitle: 'vobsub', dvdsub: 'vobsub', dvb_subtitle: 'dvb' };
    /** libbitsub layout from the player's subtitle style: size step, height. */
    function bitmapDisplay(style) {
        style = style || readSubStyle();
        return { scale: style.scale, verticalOffset: -style.pos };
    }
    // ── Loudness normalisation (stage 7: NormalizeVolume) ─────────────
    // With the profile's "Normalize volume" on, each stream's audio — the
    // embedded track a:N or the dub file — is levelled by the gain the server
    // measured for it (/api/loudness: EBU R128 towards −16 LUFS, true peak
    // ≤ −1 dBTP). The <video> is routed once through a WebAudio graph:
    // source → gain → limiter (a fast, hard DynamicsCompressor at −1 dB) →
    // output. The page's AudioContext only runs after a user gesture, and an
    // element routed into a suspended context is silent — so routing waits
    // until the context runs. A measurement still pending is asked for again
    // a few times; until then (and on any failure) the audio plays as is.
    let LOUD_CTX = null;
    function loudnessContext() {
        if (!LOUD_CTX) {
            const C = window.AudioContext || window.webkitAudioContext;
            if (!C) return null;
            try { LOUD_CTX = new C(); } catch { return null; }
        }
        return LOUD_CTX;
    }
    function whenAudioRuns(ctx) {
        if (ctx.state === 'running') return Promise.resolve(true);
        try { ctx.resume().catch(() => {}); } catch {}
        return new Promise((resolve) => {
            const done = () => { cleanup(); resolve(true); };
            const kick = () => { try { ctx.resume().then(() => { if (ctx.state === 'running') done(); }).catch(() => {}); } catch {} };
            const onState = () => { if (ctx.state === 'running') done(); };
            const cleanup = () => {
                document.removeEventListener('pointerdown', kick, true);
                document.removeEventListener('keydown', kick, true);
                try { ctx.removeEventListener('statechange', onState); } catch {}
            };
            document.addEventListener('pointerdown', kick, true);
            document.addEventListener('keydown', kick, true);
            try { ctx.addEventListener('statechange', onState); } catch {}
        });
    }
    async function setupLoudness(entry) {
        if (!PREFS.normalizeVolume || !entry || !entry.art || !entry.mediaPath) return;
        const video = entry.art.video;
        const path = entry.currentExternalAudioPath || entry.mediaPath;
        const track = entry.currentExternalAudioPath ? 0 : (entry.currentAudIdx || 0);
        let res = null;
        for (let attempt = 0; attempt < 6; attempt++) {
            if (entry.adapter == null || entry.art == null) return;   // torn down
            try {
                const r = await fetch(apiUrl('/api/loudness?path=' + encodeURIComponent(path) + '&track=' + track
                    + '&wait=' + (attempt === 0 ? 3 : 10)), { signal: entry.abort.signal });
                res = r.ok ? await r.json() : null;
            } catch { return; }
            if (!res || res.status !== 'pending') break;
            await new Promise(r => setTimeout(r, 5000));
        }
        if (!res || res.status !== 'measured' || !Number.isFinite(res.gainDb) || entry.art == null) return;
        const ctx = loudnessContext();
        if (!ctx) return;
        await whenAudioRuns(ctx);
        if (entry.art == null || entry.art.video !== video) return;
        try {
            const gainDb = Math.max(-20, Math.min(15, res.gainDb));
            const source = ctx.createMediaElementSource(video);
            const gain = ctx.createGain();
            gain.gain.value = Math.pow(10, gainDb / 20);
            const limiter = ctx.createDynamicsCompressor();
            limiter.threshold.value = -1;
            limiter.knee.value = 0;
            limiter.ratio.value = 20;
            limiter.attack.value = 0.003;
            limiter.release.value = 0.25;
            source.connect(gain);
            gain.connect(limiter);
            limiter.connect(ctx.destination);
            entry.loudness = { lufs: res.integratedLufs, gainDb, nodes: [source, gain, limiter] };
            if (entry.updateMeta) entry.updateMeta();
        } catch (e) { console.warn('animarrPlayer: loudness normalisation unavailable', e); }
    }
    function disposeLoudness(entry) {
        const l = entry && entry.loudness;
        if (!l) return;
        entry.loudness = null;
        l.nodes.forEach((n) => { try { n.disconnect(); } catch {} });
    }

    /** family (lower-case) → font URL, as JASSUB's availableFonts wants it. */
    function assFontMap(entry) {
        const map = {};
        (entry.subFonts || []).forEach((f) => {
            const url = mediaApiUrl(f.url);
            if (!url) return;
            [f.family, f.fullName, f.postScriptName].forEach((n) => {
                const k = String(n || '').trim().toLowerCase();
                if (k && !map[k]) map[k] = url;
            });
        });
        map['liberation sans'] = map['liberation sans'] || vendorUrl('jassub/default.woff2');
        return map;
    }
    /** { kind: 'text' | 'ass', url, fallbackUrl, name } for a choice
     *  ({ idx } embedded s:N | { ext } sidecar path), or null. */
    function subtitlePlan(entry, choice) {
        if (choice && choice.idx != null) {
            const s = (entry.subtitleList || [])[choice.idx];
            if (!s) return null;
            const info = (entry.subTracks || []).find(t => t.index === choice.idx) || null;
            const codec = String((info && info.codec) || s.codec || '').toLowerCase();
            const image = IMAGE_SUB_CODECS[codec] || (info && info.kind === 'image' ? 'other' : null);
            if (image) {
                if (image === 'pgs' && canDrawBitmaps())
                    return { kind: 'bitmap', format: 'pgs', name: s.name, burnIndex: choice.idx,
                             subUrl: (info && mediaApiUrl(info.supUrl))
                                 || apiUrl('/api/subtitle/sup?path=' + encodeURIComponent(entry.mediaPath) + '&track=' + choice.idx) };
                return { kind: 'burn', name: s.name, burnIndex: choice.idx };
            }
            if (codec === 'ass' || codec === 'ssa') {
                // Not analysed yet → libass, to be safe.
                const complex = info && info.ass ? !!info.ass.complex : true;
                if (complex) {
                    const url = (info && mediaApiUrl(info.assUrl))
                        || apiUrl('/api/subtitle?path=' + encodeURIComponent(entry.mediaPath) + '&track=' + choice.idx + '&format=ass');
                    return { kind: 'ass', url, fallbackUrl: s.url, name: s.name };
                }
            }
            return { kind: 'text', url: s.url, name: s.name };
        }
        if (choice && choice.ext) {
            const x = (entry.externalSubList || []).find(t => samePath(t.path, choice.ext));
            if (!x) return null;
            const ext = String(x.ext || '').toLowerCase();
            if (x.image) {
                // A sidecar can't be burned in (burnSubtitle names a track of
                // the video): drawn here or not at all.
                if (!canDrawBitmaps()) return { kind: 'none', name: x.label };
                if (ext === 'sup')
                    return { kind: 'bitmap', format: 'pgs', name: x.label,
                             subUrl: apiUrl('/api/subtitle/sup?path=' + encodeURIComponent(x.path) + '&track=0') };
                if (ext === 'idx' && x.companionPath)
                    return { kind: 'bitmap', format: 'vobsub', name: x.label,
                             idxUrl: apiUrl('/api/file?path=' + encodeURIComponent(x.path)),
                             subUrl: apiUrl('/api/file?path=' + encodeURIComponent(x.companionPath)) };
                return { kind: 'none', name: x.label };
            }
            if (ext === 'ass' || ext === 'ssa') {
                return { kind: 'ass', url: apiUrl('/api/subtitle?path=' + encodeURIComponent(x.path) + '&track=0&format=ass'),
                         fallbackUrl: externalSubUrl(x.path), name: x.label };
            }
            return { kind: 'text', url: externalSubUrl(x.path), name: x.label };
        }
        return null;
    }
    function disposeSubRenderer(entry) {
        const r = entry && entry.subRenderer;
        if (!r) return;
        entry.subRenderer = null;
        try { r.dispose(); } catch (e) { console.warn('subtitle renderer dispose failed', e); }
    }
    /** THE place a subtitle choice is put on screen — the CC menu, the
     *  initial pick, a restart. `choice`: { idx } | { ext } | null (Off). */
    /** A toast once the HUD is up (the initial pick runs before it is). */
    function laterToast(entry, title, detail) {
        setTimeout(() => { if (entry.hudRoot && entry.adapter) showToast(entry.hudRoot, title, detail); }, 1200);
    }
    /** `user`: the CC menu (may burn in); else the initial pick / a restart,
     *  which never starts a re-encode on its own. */
    async function applySubtitleChoice(entry, choice, user) {
        const token = (entry.subToken = (entry.subToken || 0) + 1);
        disposeSubRenderer(entry);
        const plan = choice ? subtitlePlan(entry, choice) : null;
        const adapter = entry.adapter;
        if (plan && plan.kind === 'burn') {
            if (user && entry.elementId) { burnSubtitleIn(entry.elementId, plan.burnIndex); return; }
            choice = null;   // nothing can show it without a re-encode
            laterToast(entry, t('sub_burn_needed'), plan.name);
        }
        if (plan && plan.kind === 'none') {
            choice = null;
            laterToast(entry, t('sub_unsupported'), plan.name);
        }
        entry.currentSubIdx     = choice && choice.idx != null ? choice.idx : null;
        entry.currentExtSubPath = choice && choice.ext ? choice.ext : null;
        if (!adapter) return;
        if (!choice || !plan) { adapter.setSubtitle(null); return; }
        if (plan.kind === 'text') {
            // Fetched here first: Artplayer reads any answer as WebVTT, so a
            // 422 (code convert_failed / image_subtitle) became an empty
            // track with no word said. The body goes on as a blob URL (no
            // second request; Artplayer revokes it on the next switch).
            try {
                const res = await fetch(plan.url, { signal: entry.abort && entry.abort.signal });
                if (entry.subToken !== token) return;
                if (!res.ok) {
                    let code = '';
                    try { const pd = await res.json(); code = (pd && pd.code) || ''; } catch {}
                    adapter.setSubtitle(null);
                    entry.currentSubIdx = null; entry.currentExtSubPath = null;
                    showToast(entry.hudRoot, t(code === 'image_subtitle' ? 'sub_unsupported' : 'sub_convert_failed'), plan.name);
                    return;
                }
                const blobUrl = URL.createObjectURL(await res.blob());
                if (entry.subToken !== token) { URL.revokeObjectURL(blobUrl); return; }
                adapter.setSubtitle({ url: blobUrl, name: plan.name, type: 'vtt' });
            } catch (e) {
                if (e && e.name === 'AbortError') return;
                if (entry.subToken === token) adapter.setSubtitle({ url: plan.url, name: plan.name, type: 'vtt' });
            }
            return;
        }
        adapter.setSubtitle(null);   // Artplayer's own overlay stays empty
        if (plan.kind === 'bitmap') {
            try {
                const lib = await loadBitsub();
                const video = entry.art && entry.art.video;
                if (entry.subToken !== token || !video) return;
                const Ctor = plan.format === 'vobsub' ? lib.VobSubRenderer : lib.PgsRenderer;
                const inst = new Ctor({
                    video,
                    subUrl: plan.subUrl,
                    ...(plan.idxUrl ? { idxUrl: plan.idxUrl } : {}),
                    container: entry.art.template.$player,
                    timeOffset: -(entry.subOffset || 0),
                    displaySettings: bitmapDisplay(),
                    onError: (e) => {
                        console.warn('animarrPlayer: bitmap subtitles failed', e);
                        if (entry.subToken === token && entry.hudRoot) showToast(entry.hudRoot, t('sub_bitmap_failed'), plan.name);
                    },
                });
                entry.subRenderer = { kind: 'bitmap', inst, dispose: () => inst.dispose() };
            } catch (e) {
                console.warn('animarrPlayer: bitmap subtitle renderer unavailable', e);
                if (entry.subToken === token && entry.hudRoot)
                    showToast(entry.hudRoot, t('sub_bitmap_failed'), plan.burnIndex != null ? t('sub_burn_hint') : plan.name);
            }
            return;
        }
        try {
            // The video's attached fonts (the tracks answer) before libass starts.
            if (entry.subTracksReady) await withTimeout(entry.subTracksReady, 5000, null);
            const JASSUB = await loadJassub();
            const video = entry.art && entry.art.video;
            if (entry.subToken !== token || !video) return;
            const inst = new JASSUB({
                video,
                subUrl: plan.url,
                workerUrl: vendorUrl('jassub/jassub-worker.bundle.js'),
                wasmUrl: vendorUrl('jassub/jassub-worker.wasm'),
                modernWasmUrl: vendorUrl('jassub/jassub-worker-modern.wasm'),
                availableFonts: assFontMap(entry),
                defaultFont: 'liberation sans',
                queryFonts: false,
                // + offset = lines later = libass renders an earlier moment.
                timeOffset: -(entry.subOffset || 0),
            });
            entry.subRenderer = { kind: 'ass', inst, dispose: () => inst.destroy() };
        } catch (e) {
            console.warn('animarrPlayer: libass renderer unavailable, showing the track as plain text', e);
            if (entry.subToken === token && plan.fallbackUrl)
                adapter.setSubtitle({ url: plan.fallbackUrl, name: plan.name, type: 'vtt' });
        }
    }

    // ── Subtitle style (task 2.7) ─────────────────────────────────────
    // Size step (scales the account's subtitle size), colour, edge
    // (outline / shadow / box / none) and how far above the bottom the cues
    // sit. A per-device look, so localStorage; applied as CSS variables /
    // data-sub-edge on Artplayer's player element (animarr-player.css).
    const SUB_STYLE_KEY = 'animarr_player_substyle';
    const SUB_SCALES = [0.75, 1, 1.25, 1.5, 2];
    const SUB_COLORS = { white: '#ffffff', yellow: '#ffe14d', cyan: '#6fe6ff', green: '#8cff7a' };
    const SUB_EDGES = ['outline', 'shadow', 'box', 'none'];
    const SUB_POS_MAX = 30;      // % of the player height
    function readSubStyle() {
        let s = null;
        try { s = JSON.parse(localStorage.getItem(SUB_STYLE_KEY) || 'null'); } catch {}
        s = s || {};
        return {
            scale: SUB_SCALES.includes(s.scale) ? s.scale : 1,
            color: SUB_COLORS[s.color] ? s.color : 'white',
            edge:  SUB_EDGES.includes(s.edge) ? s.edge : 'outline',
            pos:   Number.isFinite(s.pos) ? Math.max(0, Math.min(SUB_POS_MAX, Math.round(s.pos))) : 0,
        };
    }
    function saveSubStyle(s) {
        try { localStorage.setItem(SUB_STYLE_KEY, JSON.stringify(s)); } catch {}
    }
    function applySubStyle(player, s) {
        if (!player) return;
        s = s || readSubStyle();
        const base = PREFS.subSize > 0 ? PREFS.subSize : 20;
        player.style.setProperty('--art-subtitle-font-size', Math.round(base * s.scale) + 'px');
        player.style.setProperty('--vp-sub-color', SUB_COLORS[s.color]);
        player.style.setProperty('--vp-sub-pos', s.pos + '%');
        player.dataset.subEdge = s.edge;
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
    // The HUD (attachHud, button callbacks, autoRemoveBars, progress
    // reporter) talks to an abstract player surface rather than to
    // Artplayer directly. (The WebView-side native ExoPlayer adapter this
    // was made for is gone: the TV app plays natively in its own page, and
    // the gate here was false everywhere.) The contract:
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
    //     setView({aspect, zoom, pan})    → void   (picture geometry)
    //     rawVideoElement()               → HTMLVideoElement|null
    //                                              (WebAudio, renderers,
    //                                               buffered ranges, PiP)
    //     destroy()                       → void
    //
    // The adapter keeps the underlying handles (Artplayer instance, hls.js)
    // to itself; the HUD never touches them.

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
                    // Inert text if anything ever bypasses renderCuesSafely.
                    escape: true,
                });
                this.art.subtitle.show = true;
            } catch (e) { console.warn('subtitle switch failed', e); }
        }
        setSubtitleVisible(b) {
            try { this.art.subtitle.show = !!b; } catch {}
        }

        // ── Picture geometry (tasks 3.1 / 3.4) ────────────────────────
        // view = { aspect, zoom, pan } (see ASPECT_MODES):
        //   fit      contain — the whole frame, bars where it doesn't fill
        //   fill     cover   — fills the player, crops the overflow
        //   stretch  fill    — fills the player, distorts
        //   force:R  the frame STRETCHED to display aspect R (an anamorphic /
        //            mis-flagged file), in the largest R box that fits
        //   crop:R   the frame COVERING an R box — crops baked-in bars
        //   crop:auto "Remove black bars": an R box of the picture's own
        //            aspect inside its bars (view.bars, from /api/media-facts),
        //            the frame covering it at the picture's position — so
        //            letterbox AND pillarbox (and off-centre bars) go
        // The R box is Artplayer's aspectRatio (it sizes the <video> and
        // re-applies on resize); object-fit then maps the frame into it.
        // zoom (≥ 1) scales the picture about its centre; pan (−1…1, + =
        // the picture moves up, revealing its bottom) shifts what a zoom or
        // a cover crop pushed out of view — object-position for the cover
        // overflow, a translate for the zoom's.
        setView(view) {
            try {
                const video = this.art.video || this.art.template?.$video;
                if (!video) return;
                const { mode, ratio, auto } = parseAspect(view && view.aspect);
                const bars = auto ? (view && view.bars) || null : null;
                const want = auto ? (bars ? bars.ratio : 'default') : (ratio || 'default');
                if (this.art.aspectRatio !== want) this.art.aspectRatio = want;
                video.style.objectFit = auto ? (bars ? 'cover' : '')
                                      : (mode === 'fill' || mode === 'crop') ? 'cover'
                                      : (mode === 'stretch' || mode === 'force') ? 'fill' : '';
                const z = Math.max(1, (view && view.zoom) || 1);
                const p = Math.max(-1, Math.min(1, (view && view.pan) || 0));
                // Without bars the cover overflow is panned by object-position;
                // the bars crop uses it to pick the picture out of the frame.
                video.style.objectPosition = bars ? bars.posX.toFixed(2) + '% ' + bars.posY.toFixed(2) + '%'
                                           : p ? '50% ' + (50 + p * 50) + '%' : '';
                video.style.transform = (z !== 1 || p)
                    ? 'scale(' + z + ') translateY(' + (-p * (z - 1) / (2 * z) * 100).toFixed(3) + '%)' : '';
            } catch (e) { console.warn('picture geometry failed', e); }
        }

        // ── Raw video element (canvas sampling only) ──────────────────
        rawVideoElement() { return this.art.video || null; }

        destroy() {
            try { this.art.destroy(true); } catch {}
        }
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
                case 'nvenc-reencode':
                case 'sw-reencode':    return 'hw';     // -bf 0 + browser chain latency
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
     *  source index preserved); external entries follow, labelled by
     *  externalTrackLabel ("Russian · AniLibria · MKA · external") so the user
     *  can tell a sideload dub from a muxed track. Each option carries a
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
                label: externalTrackLabel(t),
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

    /** Read the saved playback volume (0..1): this device's own last level,
     *  else the profile's default volume, else 1.0. */
    function readVolumePref() {
        try {
            const v = parseFloat(localStorage.getItem('animarr_player_volume'));
            if (Number.isFinite(v) && v >= 0 && v <= 1) return v;
        } catch {}
        return PREFS.defaultVolume != null ? PREFS.defaultVolume / 100 : 1.0;
    }
    function saveVolumePref(v) {
        try { localStorage.setItem('animarr_player_volume', String(v)); } catch {}
    }

    // Playback speed (plan stage 7): 0.5–2×, remembered across opens.
    const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
    function readRatePref() {
        try {
            const v = parseFloat(localStorage.getItem('animarr_player_rate'));
            if (SPEEDS.includes(v)) return v;
        } catch {}
        return 1;
    }
    function saveRatePref(v) {
        try { localStorage.setItem('animarr_player_rate', String(v)); } catch {}
    }
    function applyRate(video, r) {
        if (!video) return;
        try { video.defaultPlaybackRate = r; video.playbackRate = r; } catch {}
    }
    /** "1.5×"; '' for normal speed (no badge). */
    function speedBadge(rate) {
        return rate === 1 ? '' : String(rate).replace(/^0\./, '.') + '×';
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
        speed:  '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4.2 17.5a9 9 0 1 1 15.6 0"/><path d="M12 13.5 16 9"/><circle cx="12" cy="13.5" r="1.2" fill="currentColor" stroke="none"/></svg>',
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
        // Every string comes from the player.* lang pack (t(); English
        // defaults in LABEL_DEFAULTS), key hints appended in brackets.
        const T = (k) => escapeHtml(t(k));
        const K = (k, key) => escapeHtml(t(k) + ' (' + key + ')');
        return `
<div class="vp-hud vp-hud--hidden" data-visible="false" data-style="${escapeHtml(style)}">
  <div class="vp-hud__tap"></div>
  <div class="vp-hud__top">
    <button type="button" class="vp-btn vp-btn--g vp-btn--back" data-act="back"
            aria-label="${T('back')}" title="${K('back', 'Esc')}">
      <span class="vp-glyph">${I.back}</span><span class="vp-label">${T('back')}</span>
    </button>
    <div class="vp-hud__title">
      <div class="vp-hud__kicker" data-bind="kicker"></div>
      <div class="vp-hud__name"   data-bind="name"></div>
    </div>
    <div class="vp-hud__meta" data-bind="meta"></div>
  </div>

  <div class="vp-hud__center">
    <button class="vp-hud__cplay" data-act="play" aria-label="${T('hud_play_pause')}" title="${K('hud_play_pause', t('key_space'))}">
      <span class="vp-glyph" data-bind="play-icon">${I.play}</span>
    </button>
  </div>

  <div class="vp-hud__bottom">
    <div class="vp-hud__progress">
      <span class="vp-hud__time" data-bind="cur">0:00</span>
      <div class="vp-hud__track" data-act="seek">
        <div class="vp-hud__track-buffer" data-bind="buffer"></div>
        <div class="vp-hud__track-fill"   data-bind="fill"></div>
        <div class="vp-hud__chapters"     data-bind="chapters"></div>
        <div class="vp-hud__thumb"        data-bind="thumb"></div>
        <div class="vp-hud__preview" data-bind="preview">
          <div class="vp-hud__preview-img"  data-bind="preview-img"></div>
          <div class="vp-hud__preview-chapter" data-bind="preview-chapter"></div>
          <div class="vp-hud__preview-time" data-bind="preview-time">0:00</div>
        </div>
      </div>
      <span class="vp-hud__time" data-bind="dur">0:00</span>
    </div>
    <div class="vp-hud__controls">
      <div class="vp-hud__cluster vp-hud__cluster--primary">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="back10"
                aria-label="${T('hud_back10')}" title="${K('hud_back10', '←')}">
          <span class="vp-glyph">${I.back10}</span>
          <span class="vp-icon-text" aria-hidden="true">-10</span>
          <span class="vp-label">-10 ${T('unit_s')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="prev"
                aria-label="${T('hud_prev')}" title="${K('hud_prev', '◄◄')}">
          <span class="vp-glyph">${I.prev}</span><span class="vp-label">${T('hud_prev_short')}</span>
        </button>
        <button class="vp-btn vp-btn--p vp-btn--tv" data-act="play"
                aria-label="${T('hud_play_pause')}" title="${K('hud_play_pause', t('key_space'))}">
          <span class="vp-glyph" data-bind="play-icon">${I.play}</span>
          <span class="vp-label" data-bind="play-label">${T('hud_play')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="next"
                aria-label="${T('hud_next')}" title="${K('hud_next', '►►|')}">
          <span class="vp-glyph">${I.next}</span><span class="vp-label">${T('hud_next_short')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="fwd10"
                aria-label="${T('hud_fwd10')}" title="${K('hud_fwd10', '→')}">
          <span class="vp-glyph">${I.fwd10}</span>
          <span class="vp-icon-text" aria-hidden="true">+10</span>
          <span class="vp-label">+10 ${T('unit_s')}</span>
        </button>
      </div>
      <div class="vp-hud__cluster vp-hud__cluster--secondary">
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="volume"
                aria-label="${T('hud_volume')}" title="${K('hud_volume', 'M')}" data-bind="volume-btn">
          <span class="vp-glyph" data-bind="volume-icon">${I.volume}</span>
          <span class="vp-label">${T('hud_volume')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="speed"
                aria-label="${T('hud_speed')}" title="${K('hud_speed', '< >')}" data-bind="speed-btn">
          <span class="vp-glyph">${I.speed}</span><span class="vp-label">${T('hud_speed')}</span>
          <span class="vp-badge" data-bind="speed-badge" aria-hidden="true"></span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="quality"
                aria-label="${T('quality')}" title="${T('quality')}" data-bind="quality-btn">
          <span class="vp-glyph">${I.quality}</span><span class="vp-label">${T('quality')}</span>
          <span class="vp-badge" data-bind="quality-badge" aria-hidden="true"></span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="aspect"
                aria-label="${T('picture')}" title="${T('picture')}">
          <span class="vp-glyph">${I.aspect}</span><span class="vp-label">${T('picture')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cast"
                aria-label="${T('hud_cast')}" title="${T('hud_cast_hint')}"
                data-bind="cast-btn">
          <span class="vp-glyph">${I.cast}</span><span class="vp-label">${T('hud_cast_short')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="offset"
                aria-label="${T('hud_sync')}" title="${T('hud_sync')}"
                data-bind="offset-btn">
          <span class="vp-glyph">${I.offset}</span>
          <span class="vp-icon-text" aria-hidden="true">AS</span>
          <span class="vp-label">${T('hud_sync_short')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="audio"
                aria-label="${T('hud_audio')}" title="${T('hud_audio')}">
          <span class="vp-glyph">${I.audio}</span>
          <span class="vp-icon-text" aria-hidden="true">AT</span>
          <span class="vp-label">${T('hud_audio_short')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="cc"
                aria-label="${T('subtitles')}" title="${T('subtitles')}">
          <span class="vp-glyph">${I.cc}</span>
          <span class="vp-icon-text" aria-hidden="true">CC</span>
          <span class="vp-label">${T('subtitles')}</span>
        </button>
        <button class="vp-btn vp-btn--g vp-btn--tv" data-act="info"
                aria-label="${T('hud_info')}" title="${T('hud_info_hint')}">
          <span class="vp-glyph">${I.info}</span>
          <span class="vp-icon-text" aria-hidden="true">i</span>
          <span class="vp-label">${T('hud_info_short')}</span>
        </button>
      </div>
      <button class="vp-btn vp-btn--g vp-btn--tv vp-hud__gear" data-act="settings"
              aria-label="${T('hud_settings')}" title="${T('hud_settings')}">
        <span class="vp-glyph">${I.settings}</span><span class="vp-label">${T('hud_settings')}</span>
      </button>
      <button class="vp-btn vp-btn--g vp-btn--tv vp-hud__fs" data-act="fullscreen"
              aria-label="${T('hud_fullscreen')}" title="${K('hud_fullscreen', 'F')}" data-bind="fs-btn">
        <span class="vp-glyph" data-bind="fs-icon">${I.fsEnter}</span>
        <span class="vp-label" data-bind="fs-label">${T('hud_fullscreen')}</span>
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
    // `items`: strings, or { label, cls } for a row with an extra class
    // (e.g. the CC list's "Timing & style" row).
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
        const label = (it) => (it && typeof it === 'object') ? it.label : it;
        const extra = (it) => (it && typeof it === 'object' && it.cls) ? ' ' + it.cls : '';
        popup.innerHTML = `
            <div class="vp-hud-popup__title">${escapeHtml(title)}</div>
            <div class="vp-hud-popup__list">
                ${items.map((it, i) => `
                    <button class="vp-hud-popup__item${i === currentIdx ? ' is-active' : ''}${escapeHtml(extra(it))}"
                            data-i="${i}">${escapeHtml(label(it))}</button>
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

    // ── panel popup (subtitle timing & style, picture) ────────────────
    // Same shell, placement and toggle / outside-click / ghost-click rules
    // as the pickers, but the body is built by the caller from the small
    // row / chip / stepper helpers below (DOM + textContent only). Controls
    // ignore clicks in the first 400 ms: on touch the opening tap's ghost
    // click would otherwise land on whatever control now sits under it.
    function openPanelPopup(root, anchor, title, build) {
        const existing = root.querySelector('.vp-hud-popup');
        if (existing) {
            const wasFor = existing.getAttribute('data-anchor');
            const wasPanel = existing.classList.contains('vp-hud-popup--panel');
            existing.remove();
            if (wasFor === anchor && wasPanel) return;   // same button → toggle closed
        }
        const popup = document.createElement('div');
        popup.className = 'vp-hud-popup vp-hud-popup--panel';
        popup.setAttribute('data-anchor', anchor);
        popup.appendChild(mkEl('div', 'vp-hud-popup__title', title));
        const body = mkEl('div', 'vp-panel');
        popup.appendChild(body);
        const openedAt = Date.now();
        popup.dataset.openedAt = String(openedAt);
        const ready = () => Date.now() - openedAt >= 400;
        build(body, ready);
        popup.addEventListener('mousedown', e => e.stopPropagation());
        popup.addEventListener('click',     e => e.stopPropagation());
        root.appendChild(popup);
        setTimeout(() => {
            const onDoc = (e) => {
                if (!popup.isConnected) { document.removeEventListener('click', onDoc, true); return; }
                if (!ready()) return;
                if (!popup.contains(e.target) && !e.target.closest(`[data-act="${anchor}"]`)) {
                    popup.remove();
                    document.removeEventListener('click', onDoc, true);
                }
            };
            document.addEventListener('click', onDoc, true);
        }, 0);
        return popup;
    }
    function mkEl(tag, cls, text) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }
    /** One labelled row; returns its control cell. */
    function panelRow(body, label) {
        const row = mkEl('div', 'vp-panel__row');
        row.appendChild(mkEl('div', 'vp-panel__label', label));
        const ctl = mkEl('div', 'vp-panel__ctl');
        row.appendChild(ctl);
        body.appendChild(row);
        return ctl;
    }
    /** Segmented choice. options: [{ value, label, title?, swatch? }]. */
    function panelChips(ctl, options, current, ready, onPick) {
        const btns = options.map(o => {
            const b = mkEl('button', 'vp-chip', o.swatch ? null : o.label);
            b.type = 'button';
            if (o.swatch) {
                b.classList.add('vp-chip--swatch');
                b.style.setProperty('--vp-swatch', o.swatch);
                b.setAttribute('aria-label', o.label);
            }
            if (o.title || o.swatch) b.title = o.title || o.label;
            b.addEventListener('click', () => {
                if (!ready()) return;
                btns.forEach(x => x.classList.toggle('is-active', x === b));
                onPick(o.value);
            });
            ctl.appendChild(b);
            return b;
        });
        btns.forEach((b, i) => b.classList.toggle('is-active', options[i].value === current));
        return btns;
    }
    /** − value + stepper. steps: [{ label, delta }] left of / right of the
     *  value; `format` renders it. Returns { set(v) } to refresh the value. */
    function panelStepper(ctl, value, steps, format, ready, onStep) {
        const val = mkEl('span', 'vp-panel__value', format(value));
        const mk = (s) => {
            const b = mkEl('button', 'vp-chip vp-chip--step', s.label);
            b.type = 'button';
            b.addEventListener('click', () => { if (ready()) val.textContent = format(onStep(s.delta)); });
            return b;
        };
        steps.filter(s => s.delta < 0).forEach(s => ctl.appendChild(mk(s)));
        ctl.appendChild(val);
        steps.filter(s => s.delta > 0).forEach(s => ctl.appendChild(mk(s)));
        return { set(v) { val.textContent = format(v); } };
    }

    /** Subtitles → "Timing & style": offset (±, 0.1 s steps) and the cue look.
     *  `player` = Artplayer's player element (carries the style variables). */
    function openSubtitleSettingsPopup(root, anchor, entry, player) {
        openPanelPopup(root, anchor, t('sub_settings'), (body, ready) => {
            const fmtOffset = (v) => (v > 0 ? '+' : v < 0 ? '−' : '±') + Math.abs(v).toFixed(1) + ' ' + t('unit_s');
            const offCtl = panelRow(body, t('sub_offset'));
            const stepper = panelStepper(offCtl, entry.subOffset || 0,
                [{ label: '−1', delta: -1 }, { label: '−0.1', delta: -0.1 },
                 { label: '+0.1', delta: 0.1 }, { label: '+1', delta: 1 }],
                fmtOffset, ready,
                (d) => setSubtitleOffset(entry, (entry.subOffset || 0) + d, true));
            const reset = mkEl('button', 'vp-chip vp-chip--step', t('reset'));
            reset.type = 'button';
            reset.addEventListener('click', () => { if (ready()) stepper.set(setSubtitleOffset(entry, 0, 'reset')); });
            offCtl.appendChild(reset);
            body.appendChild(mkEl('div', 'vp-panel__hint', t('sub_offset_hint')));

            // A libass track is drawn with its own styles: only timing applies.
            if (entry.subRenderer && entry.subRenderer.kind === 'ass') {
                body.appendChild(mkEl('div', 'vp-panel__hint', t('sub_styled_hint')));
                return;
            }
            // A bitmap track (PGS / VobSub) takes the size step and the height.
            const bitmap = entry.subRenderer && entry.subRenderer.kind === 'bitmap' ? entry.subRenderer : null;
            const style = readSubStyle();
            const update = (patch) => {
                Object.assign(style, patch);
                saveSubStyle(style);
                applySubStyle(player, style);
                if (bitmap) { try { bitmap.inst.setDisplaySettings(bitmapDisplay(style)); } catch {} }
            };
            panelChips(panelRow(body, t('sub_size')),
                SUB_SCALES.map(v => ({ value: v, label: Math.round(v * 100) + '%' })),
                style.scale, ready, v => update({ scale: v }));
            if (!bitmap) {
                panelChips(panelRow(body, t('sub_color')),
                    Object.keys(SUB_COLORS).map(k => ({ value: k, label: t('color_' + k), swatch: SUB_COLORS[k] })),
                    style.color, ready, v => update({ color: v }));
                panelChips(panelRow(body, t('sub_edge')),
                    SUB_EDGES.map(k => ({ value: k, label: t('edge_' + k) })),
                    style.edge, ready, v => update({ edge: v }));
            }
            const posCtl = panelRow(body, t('sub_position'));
            const range = document.createElement('input');
            range.type = 'range';
            range.min = '0'; range.max = String(SUB_POS_MAX); range.step = '1';
            range.value = String(style.pos);
            range.setAttribute('aria-label', t('sub_position'));
            range.addEventListener('input', () => update({ pos: parseInt(range.value, 10) || 0 }));
            posCtl.appendChild(range);
        });
    }

    /** Aspect button → "Picture": one mode out of Fit / Fill / Stretch, a
     *  forced display aspect (stretch) or a crop to N:M, plus zoom and
     *  vertical shift. Any pick here counts as the user's (no auto-crop
     *  over it). */
    function openPictureSettingsPopup(root, anchor, entry) {
        // Every change here is the user's: remembered for the title (2.2).
        const save = () => { if (entry.elementId) queueTrackPrefs(entry.elementId, { aspect: encodeView(entry) }); };
        openPanelPopup(root, anchor, t('picture'), (body, ready) => {
            const all = [];   // every mode chip, across the three rows: one choice
            const refresh = () => all.forEach(b => b.classList.toggle('is-active', b.dataset.value === (entry.currentAspect || 'fit')));
            const pickRow = (label, options) => {
                const ctl = panelRow(body, label);
                options.forEach(o => {
                    const b = mkEl('button', 'vp-chip', o.label);
                    b.type = 'button';
                    b.dataset.value = o.value;
                    if (o.off) { b.disabled = true; b.classList.add('is-off'); }
                    if (o.title) b.title = o.title;
                    b.addEventListener('click', () => {
                        if (!ready()) return;
                        entry.currentAspect = o.value;
                        entry.aspectPicked  = true;   // even "Fit": no auto-crop over it
                        applyView(entry);
                        refresh();
                        save();
                    });
                    ctl.appendChild(b);
                    all.push(b);
                });
            };
            pickRow(t('picture_mode'), [...ASPECT_MODES.map(m => ({ value: m, label: t('aspect_' + m) })),
                { value: BARS_KEY, label: t('aspect_nobars'), off: !barsGeometry(entry.facts),
                  title: barsGeometry(entry.facts) ? null
                       : (entry.facts && entry.facts.video && entry.facts.video.cropStatus === 'pending') ? t('nobars_pending') : t('nobars_none') }]);
            pickRow(t('picture_force'), FORCE_RATIOS.map(r => ({ value: 'force:' + r, label: r })));
            pickRow(t('picture_crop'), CROP_RATIOS.map(r => ({ value: 'crop:' + r, label: r })));
            refresh();

            const zoomCtl = panelRow(body, t('picture_zoom'));
            const zoomVal = panelStepper(zoomCtl, entry.zoom || 1,
                [{ label: '−', delta: -0.1 }, { label: '+', delta: 0.1 }],
                (z) => Math.round(z * 100) + '%', ready,
                (d) => {
                    entry.zoom = Math.round(Math.max(1, Math.min(ZOOM_MAX, (entry.zoom || 1) + d)) * 100) / 100;
                    applyView(entry);
                    save();
                    return entry.zoom;
                });
            const panCtl = panelRow(body, t('picture_shift'));
            const fmtPan = (p) => p === 0 ? '0' : (p > 0 ? '▲ ' : '▼ ') + Math.round(Math.abs(p) * 100) + '%';
            const panVal = panelStepper(panCtl, entry.pan || 0,
                [{ label: '▼', delta: -0.1 }, { label: '▲', delta: 0.1 }],
                fmtPan, ready,
                (d) => {
                    entry.pan = Math.round(Math.max(-1, Math.min(1, (entry.pan || 0) + d)) * 10) / 10;
                    applyView(entry);
                    save();
                    return entry.pan;
                });
            const reset = mkEl('button', 'vp-chip vp-chip--step', t('reset'));
            reset.type = 'button';
            reset.addEventListener('click', () => {
                if (!ready()) return;
                entry.zoom = 1; entry.pan = 0;
                applyView(entry);
                save();
                zoomVal.set(1); panVal.set(0);
            });
            zoomCtl.appendChild(reset);
            if (document.querySelector('.vp-hud[data-touch="1"]'))
                body.appendChild(mkEl('div', 'vp-panel__hint', t('picture_pinch_hint')));
        });
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
        const rows = (lines && lines.length) ? lines : [t('info_none')];
        popup.innerHTML = `
            <div class="vp-hud-popup__title">${escapeHtml(t('hud_info'))}</div>
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

    // ── audio sync popup (task 2.8) ───────────────────────────────────
    // The offset of the CURRENT audio — this embedded track or this dub —
    // for this title, ±3000 ms (+ = the sound plays later). `onChange(ms)`
    // stores it (setAudioOffset: remembered via track memory, applied with a
    // seamless restart at the same position once the user stops changing it).
    // `label` names the audio it is for.
    function openAudioSyncPopup(root, anchor, entry, label, onChange) {
        openPanelPopup(root, anchor, t('hud_sync'), (body, ready) => {
            if (label) body.appendChild(mkEl('div', 'vp-panel__hint vp-panel__hint--top', label));
            const fmt = (ms) => (ms > 0 ? '+' : ms < 0 ? '−' : '±') + Math.abs(ms) + ' ' + t('unit_ms');
            const ctl = panelRow(body, t('sync_offset'));
            const range = document.createElement('input');
            range.type = 'range';
            range.min = String(-AUDIO_OFFSET_MAX); range.max = String(AUDIO_OFFSET_MAX); range.step = '10';
            range.value = String(entry.audioOffsetMs || 0);
            range.setAttribute('aria-label', t('sync_offset'));
            const set = (ms) => {
                const v = onChange(ms);
                range.value = String(v);
                stepper.set(v);
                return v;
            };
            const stepper = panelStepper(ctl, entry.audioOffsetMs || 0,
                [{ label: '−100', delta: -100 }, { label: '−10', delta: -10 },
                 { label: '+10', delta: 10 }, { label: '+100', delta: 100 }],
                fmt, ready, (d) => set((entry.audioOffsetMs || 0) + d));
            const reset = mkEl('button', 'vp-chip vp-chip--step', t('reset'));
            reset.type = 'button';
            reset.addEventListener('click', () => { if (ready()) set(null); });
            ctl.appendChild(reset);
            const rangeRow = mkEl('div', 'vp-panel__ctl');
            rangeRow.appendChild(range);
            body.appendChild(rangeRow);
            range.addEventListener('input', () => set(parseInt(range.value, 10) || 0));
            body.appendChild(mkEl('div', 'vp-panel__hint', t('sync_hint_live')));
        });
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
            <div class="vp-hud-popup__title">${escapeHtml(t('hud_volume'))}</div>
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
            : [t('cast_none')];
        openPickerPopup(root, anchor, t('cast_title'), labels,
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

    // Picture modes (entry.currentAspect holds one of these keys; see
    // ArtplayerAdapter.setView for what each does).
    const ASPECT_MODES  = ['fit', 'fill', 'stretch'];
    const FORCE_RATIOS  = ['16:9', '4:3', '21:9', '2.35:1', '1.85:1'];
    const CROP_RATIOS   = ['21:9', '2.35:1', '1.85:1', '16:9', '4:3'];
    const ZOOM_MAX      = 3;
    const BARS_KEY      = 'crop:auto';   // "Remove black bars"
    /** The picture inside the frame's baked-in bars, from /api/media-facts
     *  (null: none detected / not known yet): the R box ratio for Artplayer,
     *  the object-position that puts the picture in it, and the fractions. */
    function barsGeometry(facts) {
        const v = facts && facts.video;
        if (!v || v.cropStatus !== 'detected' || !v.crop || !(v.contentAspect > 0) || !v.width || !v.height) return null;
        const cx = v.crop.x / v.width, cy = v.crop.y / v.height;
        const cw = v.crop.width / v.width, ch = v.crop.height / v.height;
        if (!(cw > 0 && ch > 0) || (cw > 0.995 && ch > 0.995)) return null;
        const pos = (o, size) => size < 0.995 ? Math.max(0, Math.min(100, o / (1 - size) * 100)) : 50;
        return {
            ratio: (Math.round(v.contentAspect * 10000) / 10000) + ':1',
            posX: pos(cx, cw), posY: pos(cy, ch), cw, ch,
            displayAspect: v.displayAspect || (v.width / v.height), contentAspect: v.contentAspect,
            letterbox: !!v.letterbox, pillarbox: !!v.pillarbox,
        };
    }
    /** How much larger the picture gets on this player with its bars cut
     *  (1 = no gain): contained picture area / the area it has inside the
     *  contained frame. */
    function barsGain(entry) {
        const b = barsGeometry(entry.facts);
        const pl = entry.art && entry.art.template && entry.art.template.$player;
        if (!b || !pl || !pl.clientWidth || !pl.clientHeight) return 1;
        const R = pl.clientWidth / pl.clientHeight;
        const fit = (a) => a > R ? [1, R / a] : [a / R, 1];      // a box of aspect a, contained
        const [fw, fh] = fit(b.displayAspect);
        const [pw, ph] = fit(b.contentAspect);
        return (pw * ph) / Math.max(1e-6, b.cw * fw * b.ch * fh);
    }
    /** Cut the bars by itself when that clearly enlarges the picture here
     *  (a letterboxed film on an ultrawide screen, a pillarboxed 4:3 show on
     *  a phone in portrait…) — never over the user's own or a remembered
     *  choice (viewPicked); undone again when the player's shape changes
     *  so it no longer helps. */
    function autoRemoveBars(entry) {
        if (!entry || !entry.adapter || viewPicked(entry)) return;
        const cur = entry.currentAspect || 'fit';
        if (cur !== 'fit' && cur !== BARS_KEY) return;
        const want = barsGain(entry) > 1.05 ? BARS_KEY : 'fit';
        if (want === cur) return;
        entry.currentAspect = want;
        applyView(entry);
    }
    /** 'fit' | 'fill' | 'stretch' | 'force:16:9' | 'crop:2.35:1' →
     *  { mode, ratio } (ratio null for the first three; unknown → fit). */
    function parseAspect(key) {
        const k = String(key || 'fit');
        if (k === BARS_KEY) return { mode: 'crop', ratio: null, auto: true };
        const m = /^(force|crop):(.+)$/.exec(k);
        if (m && (m[1] === 'force' ? FORCE_RATIOS : CROP_RATIOS).includes(m[2])) return { mode: m[1], ratio: m[2] };
        return { mode: ASPECT_MODES.includes(k) ? k : 'fit', ratio: null };
    }
    /** Picture state ⇄ the aspect string kept in per-title track memory
     *  (≤ 32 chars of [A-Za-z0-9:._-]): "<mode>[_z<zoom>][_p<pan>]", e.g.
     *  "fit", "crop:2.35:1", "force:4:3_z1.25_p-0.3". */
    function encodeView(entry) {
        let s = entry.currentAspect || 'fit';
        const z = entry.zoom || 1, p = entry.pan || 0;
        if (z !== 1) s += '_z' + (Math.round(z * 100) / 100);
        if (p) s += '_p' + (Math.round(p * 100) / 100);
        return s;
    }
    /** → { aspect, zoom, pan }, or null for a string this player doesn't know. */
    function decodeView(str) {
        const parts = String(str || '').split('_');
        const aspect = parts.shift();
        if (!aspect || (parseAspect(aspect).mode === 'fit' && aspect !== 'fit')) return null;
        let zoom = 1, pan = 0;
        parts.forEach(x => {
            const v = parseFloat(x.slice(1));
            if (!Number.isFinite(v)) return;
            if (x[0] === 'z') zoom = Math.max(1, Math.min(ZOOM_MAX, v));
            else if (x[0] === 'p') pan = Math.max(-1, Math.min(1, v));
        });
        return { aspect, zoom, pan };
    }
    /** Two paths name the same file (server answers vs /api/external-tracks:
     *  same source, but don't trust case / separators to match). */
    function samePath(a, b) {
        const n = (x) => String(x || '').replace(/\\/g, '/').toLowerCase();
        return !!a && n(a) === n(b);
    }

    /** Push entry's picture state (aspect, zoom, pan) to the adapter. */
    function applyView(entry) {
        if (entry && entry.adapter && entry.adapter.setView)
            entry.adapter.setView({ aspect: entry.currentAspect, zoom: entry.zoom, pan: entry.pan,
                                    bars: barsGeometry(entry.facts) });
    }
    /** Has the user touched the picture (any mode pick, zoom or pan, or a
     *  remembered picture)? Then the automatic bar cut stays out of it. */
    function viewPicked(entry) {
        return !!entry.aspectPicked || (entry.zoom || 1) !== 1 || (entry.pan || 0) !== 0;
    }

    /** Sidecar subtitle file → /api/subtitle converts it to WebVTT on the fly
     *  (a standalone .srt/.ass exposes its single subtitle stream as 0:s:0). */
    function externalSubUrl(path) {
        return apiUrl('/api/subtitle?path=' + encodeURIComponent(path) + '&track=0&format=webvtt');
    }

    /** The chapter `t` falls in, with its display label ("Chapter 3" when
     *  untitled); null when there are none. */
    function chapterAt(chapters, t) {
        let hit = null;
        for (const c of chapters || []) { if (t + 0.001 >= c.startSec) hit = c; else break; }
        return hit ? { label: (hit.title && hit.title.trim()) || tf('chapter_n', hit.index + 1) } : null;
    }

    // ── HUD controller — owns event wiring + DOM updates ──────────────
    // Takes the abstract `adapter` (PlayerAdapter contract): every playback
    // interaction goes through it, never the raw Artplayer API.
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
            qualityBtn:   $('[data-bind="quality-btn"]'),
            qualityBadge: $('[data-bind="quality-badge"]'),
            speedBtn:     $('[data-bind="speed-btn"]'),
            speedBadge:   $('[data-bind="speed-badge"]'),
            castBtn:    $('[data-bind="cast-btn"]'),
            volumeIcon: $('[data-bind="volume-icon"]'),
            fsIcon:     $('[data-bind="fs-icon"]'),
            fsLabel:    $('[data-bind="fs-label"]'),
            track:      $('[data-act="seek"]'),
            preview:     $('[data-bind="preview"]'),
            previewImg:  $('[data-bind="preview-img"]'),
            previewTime: $('[data-bind="preview-time"]'),
            previewChapter: $('[data-bind="preview-chapter"]'),
            chapters:    $('[data-bind="chapters"]'),
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
                { act: 'cc',      label: t('subtitles'), run: callbacks.cc },
                { act: 'audio',   label: t('hud_audio_short'), run: callbacks.audio },
                // The touch layout hides the Quality button: carry its badge.
                { act: 'quality', label: t('quality') + (refs.qualityBadge && refs.qualityBadge.textContent
                                      ? ' · ' + refs.qualityBadge.textContent : ''), run: callbacks.quality },
                { act: 'aspect',  label: t('picture'),   run: callbacks.aspect },
                { act: 'offset',  label: t('hud_sync'),  run: callbacks.offset },
                { act: 'volume',  label: t('hud_volume'), run: callbacks.volume },
                { act: 'speed',   label: t('hud_speed') + (refs.speedBadge && refs.speedBadge.textContent
                                      ? ' · ' + refs.speedBadge.textContent : ''), run: callbacks.speed },
                { act: 'cast',    label: t('hud_cast'),  run: callbacks.cast },
                { act: 'info',    label: t('hud_info'),  run: callbacks.info },
            ].filter(r => !hidden(r.act) && typeof r.run === 'function');
            openMenuPopup(root, 'settings', t('hud_settings'), rows);
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
            syncSubtitleLift();
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
            syncSubtitleLift();
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        }
        // Lift the subtitle overlay above the bottom control rows while the HUD
        // is visible (CSS: .art-subtitle bottom uses --vp-sub-lift). The bar's
        // padding-top is the transparent top of its scrim, so cues may sit over
        // that part — only the progress + button rows are cleared. Measured on
        // every show/hide because the bar's height depends on the layout (one
        // row on desktop, two in portrait, the slim touch bar). show() runs on
        // every mouse move, so only write when the value actually changes.
        let subLiftPx = -1;
        function syncSubtitleLift() {
            let lift = 0;
            if (hud.getAttribute('data-visible') === 'true') {
                const bar = hud.querySelector('.vp-hud__bottom');
                if (bar) {
                    const padTop = parseFloat(getComputedStyle(bar).paddingTop) || 0;
                    lift = Math.max(0, Math.round(bar.offsetHeight - padTop));
                }
            }
            if (lift === subLiftPx) return;
            subLiftPx = lift;
            root.style.setProperty('--vp-sub-lift', lift + 'px');
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
            // ── pinch-zoom (task 3.4) ───────────────────────────────
            // Two fingers on the bare video: spread / pinch zooms (1×–3×),
            // moving them together shifts the picture up / down. Registered
            // before the tap handler so the fingers' pointerups (and the
            // first one right after) never count as taps. The tap layer is
            // touch-action:none in touch mode so the browser doesn't zoom
            // the page instead.
            const touches = new Map();   // pointerId → { x, y }
            let pinch = null, gestureUntil = 0;
            const pair = () => { const [a, b] = [...touches.values()]; return { d: Math.hypot(a.x - b.x, a.y - b.y), y: (a.y + b.y) / 2 }; };
            tapEl.addEventListener('pointerdown', (e) => {
                if (e.pointerType !== 'touch') return;
                touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
                if (touches.size === 2 && callbacks.pinch && callbacks.getView) {
                    const p = pair(), v = callbacks.getView();
                    pinch = { d0: Math.max(1, p.d), y0: p.y, z0: v.zoom, p0: v.pan };
                    if (sideTapTimer) { clearTimeout(sideTapTimer); sideTapTimer = null; }
                }
            });
            tapEl.addEventListener('pointermove', (e) => {
                if (!touches.has(e.pointerId)) return;
                touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
                if (!pinch || touches.size < 2) return;
                const p = pair();
                const h = Math.max(1, tapEl.clientHeight);
                const zoom = Math.round(Math.max(1, Math.min(ZOOM_MAX, pinch.z0 * p.d / pinch.d0)) * 100) / 100;
                // Fingers moving down drag the picture down (pan − = down).
                const pan = Math.round(Math.max(-1, Math.min(1, pinch.p0 - 2 * (p.y - pinch.y0) / h)) * 100) / 100;
                callbacks.pinch(zoom, pan);
            });
            const endTouch = (e) => {
                if (!touches.delete(e.pointerId)) return;
                if (pinch && touches.size < 2) { pinch = null; gestureUntil = Date.now() + 500; }
            };
            tapEl.addEventListener('pointerup', endTouch);
            tapEl.addEventListener('pointercancel', endTouch);
            tapEl.addEventListener('pointerdown', (e) => { downX = e.clientX; downY = e.clientY; });
            tapEl.addEventListener('pointerup', (e) => {
                if (pinch || touches.size > 0 || Date.now() < gestureUntil) return;   // a pinch, not a tap
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
                if (refs.fsLabel) refs.fsLabel.textContent = t(landscape ? 'hud_exit_fullscreen' : 'hud_fullscreen');
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
                case '<':
                case '>':
                    if (callbacks.speedStep) callbacks.speedStep(e.key === '>' ? 1 : -1);
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
                case 'speed':  callbacks.speed(); break;
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
            // The bubble shows the trickplay frame and / or the chapter the
            // position falls in (with neither it stays hidden).
            const tp0 = entry.trickplay;
            const tp = tp0 && tp0.spriteUrl && tp0.count ? tp0 : null;
            const chapters = entry.chapters || [];
            if (!refs.preview || (!tp && !chapters.length)) { hideSeekPreview(); return; }
            const dur = adapter.duration || entry.totalDuration || 0;
            if (dur <= 0) { hideSeekPreview(); return; }
            const rect = refs.track.getBoundingClientRect();
            if (rect.width <= 0) return;
            const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
            const t   = dur * pct;
            const ch = chapterAt(chapters, t);
            refs.previewChapter.textContent = ch ? ch.label : '';
            refs.preview.classList.toggle('vp-hud__preview--text', !tp);
            if (!tp) {
                refs.previewTime.textContent = formatTime(t);
                const halfT = (refs.preview.offsetWidth || 80) / 2;
                refs.preview.style.left = Math.max(halfT, Math.min(rect.width - halfT, clientX - rect.left)) + 'px';
                refs.preview.classList.add('vp-hud__preview--on');
                return;
            }
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
            <button type="button" class="vp-upnext__close tv-focus" data-act="un-dismiss" aria-label="${escapeHtml(t('up_next_dismiss'))}">&times;</button>
            <div class="vp-upnext__eyebrow" data-bind="un-eyebrow"></div>
            <div class="vp-upnext__name" data-bind="un-name"></div>
            <div class="vp-upnext__actions">
              <button type="button" class="vp-upnext__btn vp-upnext__btn--skip tv-focus" data-act="un-skip" data-bind="un-skip">${escapeHtml(t('skip_credits'))}</button>
              <button type="button" class="vp-upnext__btn vp-upnext__btn--play tv-focus" data-act="un-next">
                <span data-bind="un-play">${escapeHtml(t('up_next_play'))}</span>
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
            unRefs.eyebrow.textContent = m.eyebrow || t('up_next');
            unRefs.name.textContent    = m.name || '';
            unRefs.play.textContent    = m.play || t('up_next_play');
            unRefs.skip.textContent    = entry.skipCreditsLabel || t('skip_credits');
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
            const baseLabel = upNextLabels().play || t('up_next_play');
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
                setSkip(entry.skipIntroLabel || t('skip_intro'), s.introEnd);
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
            // Buffered range (the <video>'s TimeRanges).
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
            refs.playLabel.textContent = t('hud_pause');
            show();
        });
        adapter.on('pause', () => {
            hud.querySelectorAll('[data-bind="play-icon"]').forEach(e => { e.innerHTML = I.play; });
            refs.playLabel.textContent = t('hud_play');
            // Paused → keep HUD visible.
            hud.classList.remove('vp-hud--hidden');
            hud.setAttribute('data-visible', 'true');
            root.style.cursor = '';
            applyFloatingVisibility();
            syncSubtitleLift();
            if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        });

        // Sync the fullscreen icon with the document's actual fullscreen
        // state. Listening on document covers Esc-to-exit (which doesn't
        // fire Artplayer's own 'fullscreen' event) as well as button + hotkey.
        function syncFsIcon() {
            const fs = !!document.fullscreenElement;
            if (refs.fsIcon)  refs.fsIcon.innerHTML    = fs ? I.fsExit  : I.fsEnter;
            if (refs.fsLabel) refs.fsLabel.textContent = t(fs ? 'hud_exit_fullscreen' : 'hud_fullscreen');
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
            // Chapter marks on the seek bar (gaps at each chapter start but
            // the first), placed once the duration is known.
            setChapters(list) {
                if (!refs.chapters) return;
                refs.chapters.textContent = '';
                const dur = adapter.duration || entry.totalDuration || 0;
                if (!(dur > 0)) return;
                (list || []).forEach((c) => {
                    if (!(c.startSec > 0.5) || c.startSec >= dur - 0.5) return;
                    const m = document.createElement('div');
                    m.className = 'vp-hud__chapter-mark';
                    m.style.left = (c.startSec / dur * 100) + '%';
                    refs.chapters.appendChild(m);
                });
            },
            // Quality indicator (plan 4.2): badge text, re-encoded flag, and
            // the button's tooltip / accessible name.
            // Speed badge ("1.5×", none at 1×) + tooltip.
            setSpeed(rate) {
                if (refs.speedBadge) refs.speedBadge.textContent = speedBadge(rate);
                if (refs.speedBtn) {
                    const title = t('hud_speed') + ': ' + (rate === 1 ? t('speed_normal') : rate + '×') + ' (< >)';
                    refs.speedBtn.title = title;
                    refs.speedBtn.setAttribute('aria-label', title);
                }
            },
            setQuality(badge, reencoded, title) {
                if (refs.qualityBadge) {
                    refs.qualityBadge.textContent = badge || '';
                    refs.qualityBadge.classList.toggle('vp-badge--warn', !!reencoded);
                }
                if (refs.qualityBtn && title) {
                    refs.qualityBtn.title = title;
                    refs.qualityBtn.setAttribute('aria-label', title);
                }
            },
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
    //  stream start + recovery helpers
    // ─────────────────────────────────────────────────────────────────

    // Start generations. Every attach / stream restart takes a token and the
    // newest one per element wins; detach() drops it. A start still in flight
    // when the player closes or the user moves on (Back / Next pressed while a
    // switch is warming up) sees it is stale on its next await and backs out —
    // stopping the session it started — instead of building a "ghost" player
    // inside a dead element (HLS session + keepalive living on, arrow keys
    // still hijacked by player-open).
    let OP_SEQ = 0;
    const LIVE_OP = new Map();   // elementId → newest token
    function beginOp(elementId) { const t = ++OP_SEQ; LIVE_OP.set(elementId, t); return t; }
    function isLiveOp(elementId, t) { return LIVE_OP.get(elementId) === t; }

    // ── Per-profile track memory writes (task 2.2) ───────────────────
    // Every user change of audio / dub, subtitle, subtitle offset, audio
    // offset or picture goes out as a partial PUT /api/me/track-prefs/
    // {mediaItemId} (the title row) — through MediaDetail's signed-in
    // HttpClient (SaveTrackPrefs): that REST API is cookie-only, out of reach
    // of the MAUI WebView's cross-origin fetch. Debounced ~1 s and merged per
    // player element, so dragging an offset or stepping the zoom writes once;
    // flushed at detach (player closed / next episode, whose start MediaDetail
    // holds until the save landed). A file outside any identified title
    // (mediaItemId null) isn't remembered.
    const PREFS_DEBOUNCE_MS = 1000;
    const PREFS_PENDING = new Map();   // elementId → { mediaItemId, path, ref, patch, timer }
    function queueTrackPrefs(elementId, patch) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.mediaItemId || !entry.dotnetRef) return false;
        let p = PREFS_PENDING.get(elementId);
        if (p && (p.mediaItemId !== entry.mediaItemId || p.path !== entry.mediaPath)) {
            flushTrackPrefs(elementId);
            p = null;
        }
        if (!p) {
            p = { mediaItemId: entry.mediaItemId, path: entry.mediaPath, ref: entry.dotnetRef, patch: {}, timer: null };
            PREFS_PENDING.set(elementId, p);
        }
        mergePrefsPatch(p.patch, patch);
        clearTimeout(p.timer);
        p.timer = setTimeout(() => flushTrackPrefs(elementId), PREFS_DEBOUNCE_MS);
        return true;
    }
    // A PUT stores values as given (an explicit 0 is remembered) and forgets
    // facets named in `clear`, which the server applies after the values —
    // so in a merged patch the later of "set X" / "forget X" must win.
    const PREFS_CLEAR_KEY = { audio: 'audio', subtitle: 'subtitle', audioOffsetMs: 'audioOffset',
                              subtitleOffsetMs: 'subtitleOffset', aspect: 'aspect' };
    function mergePrefsPatch(into, patch) {
        Object.keys(patch).forEach((k) => {
            if (k === 'clear') {
                const add = patch.clear || [];
                Object.keys(PREFS_CLEAR_KEY).forEach((f) => { if (add.includes(PREFS_CLEAR_KEY[f])) delete into[f]; });
                into.clear = Array.from(new Set([...(into.clear || []), ...add]));
                return;
            }
            into[k] = patch[k];
            if (into.clear && PREFS_CLEAR_KEY[k]) {
                into.clear = into.clear.filter(c => c !== PREFS_CLEAR_KEY[k]);
                if (!into.clear.length) delete into.clear;
            }
        });
    }
    /** Send what's queued for this element now; resolves to the server's ok. */
    function flushTrackPrefs(elementId) {
        const p = PREFS_PENDING.get(elementId);
        if (!p) return Promise.resolve(false);
        PREFS_PENDING.delete(elementId);
        clearTimeout(p.timer);
        try {
            return Promise.resolve(p.ref.invokeMethodAsync('SaveTrackPrefs', p.mediaItemId,
                    Object.assign({ path: p.path }, p.patch)))
                .catch((e) => { console.warn('animarrPlayer: track prefs save failed', e); return false; });
        } catch (e) {
            console.warn('animarrPlayer: track prefs save failed', e);
            return Promise.resolve(false);
        }
    }
    /** The TrackChoiceRequest for an audio pick. */
    function audioChoice(externalPath, index) {
        return externalPath ? { kind: 'external', path: externalPath } : { kind: 'embedded', index: Math.max(0, index | 0) };
    }

    function stopSession(token) {
        if (!token) return;
        try {
            fetch(apiUrl('/api/hls/' + encodeURIComponent(token)),
                { method: 'DELETE', keepalive: true }).catch(() => {});
        } catch {}
    }

    // Player strings. MediaDetail passes the localized "player.*" pack (keys
    // without the prefix) with every attach; these English defaults cover a
    // host that doesn't (and keys a stale pack lacks).
    let LABELS = {};
    const LABEL_DEFAULTS = {
        error_quality:      "Couldn't switch quality, staying on the previous one",
        error_audio:        "Couldn't switch the audio track, staying on the previous one",
        error_start:        "Playback couldn't start",
        error_playback:     'Playback failed',
        error_retry:        'Retry',
        back:               'Back',
        fallback_switched:  'Playback problem — switched to a more compatible stream',
        fallback_restarted: 'Connection lost — the stream was restarted',
        subtitles:          'Subtitles',
        sub_off:            'Off',
        sub_settings:       'Timing & style',
        sub_offset:         'Timing',
        sub_offset_hint:    '+ shows the lines later, − earlier',
        sub_size:           'Size',
        sub_color:          'Colour',
        sub_edge:           'Background',
        sub_position:       'Height',
        color_white:        'White',
        color_yellow:       'Yellow',
        color_cyan:         'Cyan',
        color_green:        'Green',
        edge_outline:       'Outline',
        edge_shadow:        'Shadow',
        edge_box:           'Box',
        edge_none:          'None',
        unit_s:             's',
        reset:              'Reset',
        sub_burn_in:        'burn in',
        sub_burn_needed:    "These subtitles can only be shown burned into the picture — pick “burn in” under Subtitles",
        sub_burn_hint:      'Pick “burn in” under Subtitles to have the server draw them into the picture',
        sub_unsupported:    "These subtitles can't be shown on this device",
        sub_bitmap_failed:  "Couldn't draw these subtitles",
        sub_convert_failed: "Couldn't read these subtitles",
        error_burn:         "Couldn't burn the subtitles in, staying on the previous stream",
        sub_styled_hint:    'This track is drawn with its own ASS styles and fonts; size and colour apply to plain text tracks.',
        track_n:            'Track {0}',
        chapter_n:          'Chapter {0}',
        track_default:      'default',
        track_forced:       'forced',
        track_commentary:   'commentary',
        track_external:     'external',
        quality:            'Quality',
        quality_original:   'Original',
        unit_mbps:          'Mbps',
        info_buffer:        'Buffer: {0} s ahead, {1} s back',
        info_loudness:      'Loudness: {0} LUFS, levelled {1} dB',
        reason_user_quality_cap:      'quality limited by your choice',
        reason_user_bitrate_cap:      'bitrate limited by your choice',
        reason_codec_unsupported:     "this device can't decode the video",
        reason_dolby_vision:          'Dolby Vision adapted for this device',
        reason_interlaced:            'deinterlaced',
        reason_burned_subtitle:       'subtitles burned into the picture',
        reason_container_unsupported: 'repackaged for the browser (picture untouched)',
        reason_audio_unsupported:     'audio converted for this device',
        reason_audio_track:           'another audio track',
        reason_external_audio:        'external dub mixed in',
        reason_audio_sync:            'remuxed to apply the remembered audio sync',
        reason_hdr_tonemap:           'HDR tone-mapped to SDR',
        reason_hdr_lost:              'HDR lost',
        // HUD (buttons, tooltips, menus)
        hud_play_pause:     'Play / pause',
        hud_play:           'Play',
        hud_pause:          'Pause',
        key_space:          'Space',
        hud_back10:         'Back 10 seconds',
        hud_fwd10:          'Forward 10 seconds',
        hud_prev:           'Previous episode',
        hud_prev_short:     'Prev',
        hud_next:           'Next episode',
        hud_next_short:     'Next',
        hud_volume:         'Volume',
        hud_speed:          'Speed',
        speed_normal:       'Normal',
        hud_cast:           'Cast to TV',
        hud_cast_hint:      'Send video to a TV (DLNA)',
        hud_cast_short:     'Cast',
        hud_sync:           'Audio sync',
        hud_sync_short:     'Sync',
        hud_audio:          'Audio tracks',
        hud_audio_short:    'Audio',
        hud_info:           'Media info',
        hud_info_hint:      'Media info (codec, HDR, audio)',
        hud_info_short:     'Info',
        hud_settings:       'Settings',
        hud_fullscreen:     'Fullscreen',
        hud_exit_fullscreen:'Exit fullscreen',
        skip_intro:         'Skip intro',
        skip_credits:       'Skip credits',
        up_next:            'Up next',
        up_next_play:       'Play next',
        up_next_dismiss:    'Dismiss',
        cast_title:         'Send to TV',
        cast_none:          '(No DLNA devices found on the network)',
        sync_offset:        'Offset',
        sync_hint_live:     '+ plays the sound later, − earlier. Applied in a moment: the stream restarts at the same spot.',
        unit_ms:            'ms',
        error_sync:         "Couldn't apply the audio sync, staying on the previous one",
        error_superseded:   'Another start of this video on this device took over',
        info_none:          'No media info available',
        info_resolution:    'Resolution',
        info_video:         'Video',
        info_audio:         'Audio',
        info_playback:      'Playback',
        info_reason:        'Reason',
        info_caps:          'Client caps',
        info_caps_none:     '(not detected)',
        error_network:      'Network error',
        error_lib:          'The player library failed to load',
        error_empty:        'The server sent an empty start response',
        buffer_mobile:      'phone',
        buffer_metered:     'metered connection',
        buffer_saver:       'Data Saver',
        picture:            'Picture',
        picture_mode:       'Scaling',
        picture_force:      'Force aspect (stretch)',
        picture_crop:       'Crop to',
        picture_zoom:       'Zoom',
        picture_shift:      'Vertical shift',
        picture_pinch_hint: 'Pinch with two fingers to zoom, move them to shift',
        aspect_fit:         'Fit',
        aspect_fill:        'Fill (zoom)',
        aspect_stretch:     'Stretch',
        aspect_nobars:      'Remove black bars',
        nobars_none:        'No black bars in this picture',
        nobars_pending:     'Still measuring the black bars — try again in a moment',
    };
    function t(key) {
        return LABELS[key] || LABEL_DEFAULTS[key] || key;
    }
    /** t() with "{0}", "{1}"… placeholders (the lang packs' format). */
    function tf(key, ...args) {
        return t(key).replace(/\{(\d+)\}/g, (m, i) => (args[i] != null ? String(args[i]) : m));
    }

    // ── Client capabilities (plan §6.1, task 1.2) ─────────────────────
    // What THIS browser plays without the server touching the stream,
    // measured once per page session and sent with every start as the same
    // `nativeCaps` token list the TV app sends (TvCodecCaps.cs), so the
    // server's Direct Play / copy decision rests on facts instead of the two
    // HEVC booleans alone (AV1 / VP9 / VP8 / WebM were always transcoded).
    //   containers  mp4 m4v mov webm — only what <video src> really opens
    //   video       h264 hevc hevc10 av1 vp9 vp8 — MediaCapabilities says
    //               supported AND smooth at 1080p (a codec this machine only
    //               decodes choppily is better left to the server); the
    //               largest smooth height per codec (2160 / 1080) is kept for
    //               the Info line and a future server parameter
    //   audio       aac mp3 opus vorbis flac ac3 eac3
    //   hdr         the display is HDR (matchMedia dynamic-range: high)
    //   dv          the browser decodes Dolby Vision itself (Safari dvh1)
    //   channels    AudioContext().destination.maxChannelCount
    // Without MediaCapabilities: canPlayType / MediaSource.isTypeSupported.
    const CAPS_VIDEO = [
        // token    1080p contentType                        2160p contentType
        ['h264',   'video/mp4; codecs="avc1.640028"',        'video/mp4; codecs="avc1.640033"'],
        ['hevc',   'video/mp4; codecs="hvc1.1.6.L120.B0"',   'video/mp4; codecs="hvc1.1.6.L153.B0"'],
        ['hevc10', 'video/mp4; codecs="hvc1.2.4.L120.B0"',   'video/mp4; codecs="hvc1.2.4.L153.B0"'],
        ['av1',    'video/mp4; codecs="av01.0.08M.08"',      'video/mp4; codecs="av01.0.12M.08"'],
        ['vp9',    'video/webm; codecs="vp09.00.40.08"',     'video/webm; codecs="vp09.00.51.08"'],
        ['vp8',    'video/webm; codecs="vp8"',               null],
    ];
    const CAPS_AUDIO = [
        ['aac',    'audio/mp4; codecs="mp4a.40.2"'],
        ['mp3',    'audio/mpeg'],
        ['opus',   'audio/webm; codecs="opus"'],
        ['vorbis', 'audio/webm; codecs="vorbis"'],
        ['flac',   'audio/mp4; codecs="flac"'],
        ['ac3',    'audio/mp4; codecs="ac-3"'],
        ['eac3',   'audio/mp4; codecs="ec-3"'],
    ];
    let CAPS = null;            // resolved ClientCaps (this page session)
    let CAPS_PROMISE = null;

    function canPlayAny(ct) {
        try {
            if (document.createElement('video').canPlayType(ct) !== '') return true;
            if (window.MediaSource && MediaSource.isTypeSupported && MediaSource.isTypeSupported(ct)) return true;
        } catch {}
        return false;
    }
    function withTimeout(p, ms, fallback) {
        return Promise.race([p, new Promise(r => setTimeout(() => r(fallback), ms))]);
    }
    /** MediaCapabilities.decodingInfo for a `file` (<video src>) source →
     *  { supported, smooth, powerEfficient }, or null when unavailable. */
    async function decodeInfo(kind, ct, w, h) {
        const mc = navigator.mediaCapabilities;
        if (!mc || typeof mc.decodingInfo !== 'function' || !ct) return null;
        const cfg = kind === 'video'
            ? { type: 'file', video: { contentType: ct, width: w, height: h, bitrate: h >= 2160 ? 25e6 : 8e6, framerate: 30 } }
            : { type: 'file', audio: { contentType: ct, channels: '2', bitrate: 192000, samplerate: 48000 } };
        try {
            const r = await withTimeout(mc.decodingInfo(cfg), 1500, null);
            return r ? { supported: !!r.supported, smooth: !!r.smooth, powerEfficient: !!r.powerEfficient } : null;
        } catch { return null; }
    }
    function maxAudioChannels() {
        try {
            const C = window.AudioContext || window.webkitAudioContext;
            if (!C) return 2;
            const ctx = new C();
            const n = (ctx.destination && ctx.destination.maxChannelCount) || 2;
            try { ctx.close(); } catch {}
            return Math.max(1, n);
        } catch { return 2; }
    }
    async function detectClientCaps() {
        const caps = { containers: [], video: {}, audio: {}, tokens: [], hdr: false, dv: false, channels: 2, source: 'canPlayType' };
        if (canPlayAny('video/mp4')) caps.containers.push('mp4', 'm4v', 'mov');
        if (canPlayAny('video/webm')) caps.containers.push('webm');
        await Promise.all(CAPS_VIDEO.map(async ([tok, ct1080, ct2160]) => {
            const at1080 = await decodeInfo('video', ct1080, 1920, 1080);
            let info;
            if (at1080) {
                caps.source = 'mediaCapabilities';
                const at2160 = (at1080.supported && at1080.smooth && ct2160)
                    ? await decodeInfo('video', ct2160, 3840, 2160) : null;
                info = {
                    supported: at1080.supported && at1080.smooth,
                    powerEfficient: at1080.powerEfficient,
                    maxHeight: !at1080.supported || !at1080.smooth ? 0
                             : (at2160 && at2160.supported && at2160.smooth) ? 2160 : 1080,
                };
            } else {
                const ok = canPlayAny(ct1080);
                info = { supported: ok, powerEfficient: false, maxHeight: ok ? 1080 : 0 };
            }
            caps.video[tok] = info;
        }));
        await Promise.all(CAPS_AUDIO.map(async ([tok, ct]) => {
            const r = await decodeInfo('audio', ct);
            caps.audio[tok] = { supported: r ? r.supported : canPlayAny(ct) };
        }));
        try { caps.hdr = !!(window.matchMedia && matchMedia('(dynamic-range: high)').matches); } catch {}
        caps.dv = canPlayAny('video/mp4; codecs="dvh1.05.06"');
        caps.channels = maxAudioChannels();
        caps.tokens = [
            ...caps.containers,
            ...CAPS_VIDEO.map(v => v[0]).filter(t => caps.video[t].supported),
            ...CAPS_AUDIO.map(a => a[0]).filter(t => caps.audio[t].supported),
            ...(caps.hdr ? ['hdr'] : []),
            ...(caps.dv ? ['dv'] : []),
        ];
        return caps;
    }
    /** ClientCaps for this page session (detected once, then cached). */
    function clientCaps() {
        if (!CAPS_PROMISE) {
            CAPS_PROMISE = detectClientCaps()
                .then(c => (CAPS = c))
                .catch(e => { console.warn('animarrPlayer: caps detection failed', e); CAPS_PROMISE = null; return null; });
        }
        return CAPS_PROMISE;
    }
    /** For a start that shouldn't wait long: the cached caps, else the
     *  detection result if it lands within `ms`, else null (the server then
     *  uses its browser defaults, exactly as before). */
    async function clientCapsWithin(ms) {
        if (CAPS) return CAPS;
        return withTimeout(clientCaps(), ms, null);
    }
    /** One-line summary for the Info popup. */
    function describeCaps(caps) {
        if (!caps) return t('info_caps') + ': ' + t('info_caps_none');
        const uhd = Object.keys(caps.video).filter(t => caps.video[t].maxHeight >= 2160);
        return t('info_caps') + ': ' + caps.tokens.join(' ')
            + (uhd.length ? ' · 4K: ' + uhd.join(',') : '')
            + ' · ' + caps.channels + 'ch'
            + (caps.source === 'mediaCapabilities' ? '' : ' · (canPlayType)');
    }

    // ── hls.js buffer profile (task 4.5, B23) ────────────────────────
    // hls.js buffers forward up to maxBufferLength seconds, and keeps going
    // while the buffer holds less than maxBufferSize bytes — up to
    // maxMaxBufferLength. The old 90 s / 600 s / 120 MB let a stream-copy
    // plan (segments ready as fast as the disk reads them) pull most of an
    // episode at once. Now a minute ahead on a desktop / TV, half that on a
    // phone, less again when the browser says the connection is metered,
    // slow or in Data Saver (navigator.connection: saveData, type
    // 'cellular', effectiveType 2g/3g).
    function bufferProfile() {
        const c = navigator.connection || null;
        const isTv = document.documentElement.classList.contains('animarr-tv-host');
        const mobile = !isTv && ((navigator.userAgentData && navigator.userAgentData.mobile)
            || /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent || ''));
        if (c && c.saveData)
            return { kind: 'saver', ahead: 20, aheadMax: 30, bytes: 20e6, back: 10 };
        if (c && (c.type === 'cellular' || /^(slow-2g|2g|3g)$/.test(c.effectiveType || '')))
            return { kind: 'metered', ahead: 20, aheadMax: 40, bytes: 25e6, back: 10 };
        if (mobile)
            return { kind: 'mobile', ahead: 30, aheadMax: 60, bytes: 40e6, back: 20 };
        return { kind: 'default', ahead: 60, aheadMax: 120, bytes: 80e6, back: 30 };
    }

    // Fallback ladder (task 1.7): Direct Play → Direct Stream → HLS copy →
    // full transcode. A stream's rung comes from output.plan; a failure moves
    // one rung down, never up (the floor is kept per player element).
    const FALLBACK_DIRECT    = 0;
    const FALLBACK_REMUX     = 1;
    const FALLBACK_COPY      = 2;
    const FALLBACK_TRANSCODE = 3;
    function tierOfPlan(output, started) {
        switch (output && output.plan) {
            case 'directplay':   return FALLBACK_DIRECT;
            case 'directstream': return FALLBACK_REMUX;
            case 'ts-copy':
            case 'fmp4-copy':    return FALLBACK_COPY;
            case undefined:
            case null:
            case '':
                // No output block (older server): judge by the URL kind.
                return started && started.directPlayUrl ? FALLBACK_DIRECT
                     : started && started.directStreamUrl ? FALLBACK_REMUX : FALLBACK_COPY;
            default:             return FALLBACK_TRANSCODE;   // *-reencode
        }
    }

    // ── Server contract for the caps — the ONE place to adjust ────────
    // nativeCaps is the TV's token list (the server already bases Direct
    // Play / Direct Stream on it). The audio side is still being designed on
    // the server: the channel count travels as `maxAudioChannels` and the
    // display / DV abilities as the `hdr` / `dv` tokens — rename them here
    // when that contract lands. clientHevc / clientHevc10 (MSE-based, used
    // for the HLS plan choice) stay as they were.
    //
    // `tier` is the fallback step (task 1.7, see FALLBACK_*): each one strips
    // what the failed stream relied on, so the server's existing decision
    // lands one rung lower without a new parameter:
    //   1 no Direct Play  — container tokens dropped → the server remuxes
    //                       (Direct Stream) when it can still copy the video
    //   2 no native video — video tokens dropped → HLS, stream-copied where
    //                       the plan allows (clientHevc still applies)
    //   3 transcode       — startStream adds a bitrate cap, which always
    //                       re-encodes
    function capsQuery(caps, tier) {
        ensureHevcCaps();
        let q = (window.__animarrHevcOk ? '&clientHevc=1' : '')
              + (window.__animarrHevc10Ok ? '&clientHevc10=1' : '');
        let tokens = caps ? caps.tokens.slice() : [];
        if (tier >= 1) {
            if (!caps) tokens = ['h264', 'aac'];   // detection failed: the safe minimum
            const drop = new Set(['mp4', 'm4v', 'mov', 'webm']);
            if (tier >= 2) {
                CAPS_VIDEO.forEach(v => drop.add(v[0]));
                drop.add('dv');
            }
            tokens = tokens.filter(tk => !drop.has(tk));
            if (!tokens.length) tokens = ['aac'];  // keep the list non-empty: it selects the caps path
        }
        if (tokens.length) {
            q += '&nativeCaps=' + encodeURIComponent(tokens.join(','))
               + '&maxAudioChannels=' + (caps ? caps.channels : 2);
        }
        return q;
    }

    // HEVC decode capability — tell the server it can ship HEVC as a
    // stream-copy (Direct Stream, original quality) rather than re-encoding it
    // to H.264. Constant per browser → memoize on window. hls.js gates on
    // MediaSource.isTypeSupported; Safari/native HLS via canPlayType.
    function ensureHevcCaps() {
        if (window.__animarrHevcOk !== undefined) return;
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

    // Audio-sync offsets sent with every start.
    // Auto-calibration disabled 2026-05-27 — the WebAudio analyser-based
    // measure() is producing nonsense values (0 or 40ms when reality is
    // 400ms+) so it makes audio sync WORSE than the static default. The
    // helper module stays around for getCached/setManual used by the Sync
    // popup; only the calibrate() call is bypassed.
    // Defaults: HW 70ms (empirical baseline for VAAPI/NVENC re-encode),
    // SW 0ms (HEVC 10-bit stream-copy has no sensible default — user must
    // dial in via Sync slider).
    function readAudioOffsets() {
        let hw = 70, sw = 0;
        if (window.animarrCalibrate) {
            const h = window.animarrCalibrate.getCached('hw');
            if (h !== null) hw = h;
            const s = window.animarrCalibrate.getCached('sw');
            if (s !== null) sw = s;
        }
        return { hw, sw };
    }

    // ── Audio sync per audio (task 2.8) ───────────────────────────────
    // Each audio of a title — an embedded track or a dub — has its own sync
    // offset (± AUDIO_OFFSET_MAX ms, + = the sound later): remembered in the
    // profile's track memory (PUT audioOffsetMs) and, while nothing is, a
    // dub's "DELAY Nms" from its file name. The server applies an offset only
    // when it starts a stream, on the channel its plan uses (hw = re-encode,
    // sw = fMP4 copy and every external dub; Direct Play / Direct Stream /
    // TS copy of the file's own audio take none), so the per-audio value
    // rides on top of the pipeline baseline above in BOTH channels, and a
    // change is applied by a seamless restart at the same position.
    const AUDIO_OFFSET_MAX = 3000;
    function audioOffsetsFor(ms) {
        const base = readAudioOffsets();
        const v = Math.round(Number(ms) || 0);
        return { hw: base.hw + v, sw: base.sw + v };
    }
    /** The offset track defaults name for their audio: remembered (0
     *  included), else the dub's DELAY from its name, else 0 — the server
     *  reports that effective value (audioOffsetSource); an older one sent
     *  only the remembered value, so the DELAY is added here. */
    function defaultAudioOffset(td) {
        if (!td || !td.audio) return 0;
        if (td.audioOffsetSource) return td.audioOffsetMs || 0;
        return td.audioOffsetMs || (td.audio.kind === 'external' && td.audio.delayMs) || 0;
    }
    function dubDelayMs(entry, path) {
        const x = (entry.externalAudioList || []).find(t => samePath(t.path, path));
        return (x && x.delayMs) || 0;
    }
    /** The Sync control changed the current audio's offset: shown at once,
     *  remembered (debounced PUT), applied by a restart once the user has
     *  stopped changing it for a moment. Returns the clamped value. */
    /** `ms` null = Reset: forget the remembered value (the dub's DELAY or 0
     *  applies again); any number — 0 included — is remembered as is. */
    function setAudioOffset(elementId, ms) {
        const entry = WIRED.get(elementId);
        if (!entry) return 0;
        const reset = ms == null;
        if (reset) ms = entry.currentExternalAudioPath ? dubDelayMs(entry, entry.currentExternalAudioPath) : 0;
        const v = Math.max(-AUDIO_OFFSET_MAX, Math.min(AUDIO_OFFSET_MAX, Math.round((Number(ms) || 0) / 10) * 10));
        entry.audioOffsetMs = v;
        queueTrackPrefs(elementId, reset ? { clear: ['audioOffset'] } : { audioOffsetMs: v });
        const kept = keepFor(elementId);
        clearTimeout(kept.syncTimer);
        kept.syncTimer = setTimeout(() => applyAudioOffset(elementId), 1200);
        return v;
    }
    async function applyAudioOffset(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter || (entry.audioOffsetMs || 0) === (entry.streamAudioOffsetMs || 0)) return;
        // The panel lives in the stream's player element, which the restart
        // replaces: open it again on the new one if it was up.
        const wasOpen = !!(entry.hudRoot && entry.hudRoot.querySelector('.vp-hud-popup[data-anchor="offset"]'));
        const want = entry.audioOffsetMs || 0;
        const ok = await restartStream(elementId, { audioOffsetMs: want }, 'sync');
        const now = WIRED.get(elementId);
        if (!ok) {
            // Kept the previous stream: it — and the memory — keep its offset.
            if (now === entry && (entry.audioOffsetMs || 0) === want) {
                entry.audioOffsetMs = entry.streamAudioOffsetMs || 0;
                queueTrackPrefs(elementId, { audioOffsetMs: entry.audioOffsetMs });
            }
            return;
        }
        if (wasOpen && now && now.hud && now.hud.hud) {
            const btn = now.hud.hud.querySelector('[data-act="offset"]');
            if (btn && btn.style.display !== 'none') btn.click();
        }
    }
    /** The user picked another audio / dub: remember it, look up ITS sync
     *  offset (track memory → the dub's DELAY → 0), restart onto it. A switch
     *  that fails puts the previous choice back into the memory. */
    async function chooseAudio(elementId, sel) {
        const entry = WIRED.get(elementId);
        if (!entry) return false;
        const ext = (sel && sel.externalAudioPath) || null;
        const index = ext ? 0 : Math.max(0, (sel && sel.audioTrackIndex) | 0);
        const prevChoice = audioChoice(entry.currentExternalAudioPath, entry.currentAudIdx);
        let offsetMs = ext ? dubDelayMs(entry, ext) : 0;
        if (entry.mediaItemId && queueTrackPrefs(elementId, { audio: audioChoice(ext, index) })) {
            await flushTrackPrefs(elementId);
            let td = null;
            try { td = await entry.dotnetRef.invokeMethodAsync('ResolveTrackPrefs', entry.mediaPath); } catch {}
            const a = td && td.audio;
            const same = a && (ext ? a.kind === 'external' && samePath(a.path, ext)
                                   : a.kind === 'embedded' && (a.index | 0) === index);
            if (same) offsetMs = defaultAudioOffset(td);
        }
        if (WIRED.get(elementId) !== entry) return false;   // closed / next episode meanwhile
        const ok = await restartStream(elementId,
            { audioTrackIndex: index, externalAudioPath: ext, audioOffsetMs: offsetMs }, 'audio');
        if (!ok && entry.mediaItemId) queueTrackPrefs(elementId, { audio: prevChoice });
        return ok;
    }

    /** A stream whose tail can't be loaded: play to the end of what is
     *  buffered, then fire the element's 'ended' so Up Next, autoplay and the
     *  progress report treat it as the end of the episode. */
    function endAtBufferedTail(video) {
        if (!video || video.__animarrTailEnd) return;
        video.__animarrTailEnd = true;
        const timer = setInterval(() => {
            if (!video.isConnected) { clearInterval(timer); return; }
            const b = video.buffered;
            const bufferedEnd = b && b.length ? b.end(b.length - 1) : 0;
            if (bufferedEnd - video.currentTime > 0.35) return;
            clearInterval(timer);
            try { video.pause(); } catch {}
            video.dispatchEvent(new Event('ended'));
        }, 400);
    }

    // How long a first open answered 409 `superseded` waits for the newer
    // start to show up on its element before it reports the failure.
    const SUPERSEDED_GRACE_MS = 2500;

    function abortError() {
        try { return new DOMException('aborted', 'AbortError'); }
        catch { const e = new Error('aborted'); e.name = 'AbortError'; return e; }
    }

    /**
     * POST /api/hls/start for one playback plan. Resolves to
     *   { totalDuration, resumeSec, output, directPlayUrl, directStreamUrl,
     *     sessionToken, manifestUrl }
     * or throws an Error with `.status` (HTTP status, 0 = network), `.code`
     * (the problem+json `code` extension: invalid_audio_track,
     * invalid_max_height, invalid_max_bitrate, ffmpeg_exited, start_failed,
     * too_many_sessions, ffmpeg_timeout, superseded) and a short `.reason`
     * for the user (the problem's `detail`). One retry on a network error or
     * a 5xx that a second try can fix (a start racing the server's cleanup of
     * the previous session often succeeds a beat later) — never on 504
     * ffmpeg_timeout (the server already waited 60 s; a retry just doubles
     * that) or ffmpeg_exited (the same plan fails the same way). 4xx are
     * never retried. 409 `superseded` (a newer start for the same file from
     * this device became playable first and retired this one) is flagged
     * `.superseded`: no toast, no retry, no fallback rung — whoever started
     * the newer stream owns playback. A switch drops it; an open lets the
     * newer start render and reports only when none shows up (attachStream).
     * Aborting `signal` cancels the request — the server then stops the ffmpeg
     * it was starting (HlsEndpoints passes RequestAborted to StartAsync).
     */
    async function startStream(mediaPath, p, signal) {
        const tier = p.tier || 0;
        // Fallback tier 3: nothing that copies worked — a bitrate cap forces
        // the re-encode (unless a quality cap already does).
        const maxBitrate = (tier >= FALLBACK_TRANSCODE && !(p.maxBitrate > 0) && !(p.maxHeight > 0))
            ? (p.fallbackMbps || 8) : (p.maxBitrate || 0);
        // Audio: a fresh open asks for the profile's track defaults (usePrefs:
        // title memory → profile language → flags, task 2.2 — the server starts
        // on that audio directly, no second start); a restart names the audio
        // it is on, index 0 included, so the server never re-resolves it.
        const audioQuery = p.externalAudioPath
            ? '&externalAudio=' + encodeURIComponent(p.externalAudioPath)
            : p.usePrefs && !(p.audioTrackIndex > 0) ? '&usePrefs=1'
            : '&audioTrackIndex=' + Math.max(0, p.audioTrackIndex | 0);
        const url = apiUrl('/api/hls/start?path=' + encodeURIComponent(mediaPath)
            + (p.resumeSec > 0 ? '&seek=' + p.resumeSec.toFixed(2) : '')
            + '&audioOffsetHwMs=' + p.offsets.hw
            + '&audioOffsetSwMs=' + p.offsets.sw
            + audioQuery
            + (p.maxHeight > 0 ? '&maxHeight=' + p.maxHeight : '')
            + (maxBitrate > 0 ? '&maxBitrate=' + maxBitrate : '')
            + (Number.isFinite(p.burnSubtitle) && p.burnSubtitle >= 0 ? '&burnSubtitle=' + p.burnSubtitle : '')
            + capsQuery(p.caps, tier));
        let lastErr = null;
        for (let attempt = 0; attempt < 2; attempt++) {
            if (attempt > 0) {
                await new Promise(r => setTimeout(r, 500));
                if (signal && signal.aborted) throw abortError();
            }
            let res;
            try {
                res = await fetch(url, { method: 'POST', signal });
            } catch (err) {
                if (err && err.name === 'AbortError') throw err;
                lastErr = Object.assign(new Error('start: network error'), { status: 0, reason: t('error_network') });
                continue;
            }
            if (!res.ok) {
                // RFC 7807 problem+json: `detail` is the server's own one-line
                // reason, `code` says which failure it was.
                let why = '', code = '';
                try {
                    const pd = await res.json();
                    why  = (pd && (pd.detail || pd.title)) || '';
                    code = (pd && typeof pd.code === 'string') ? pd.code : '';
                } catch {}
                lastErr = Object.assign(new Error('start ' + res.status + (code ? ' ' + code : '')), {
                    status: res.status,
                    code,
                    reason: (why ? why + ' ' : '') + '(HTTP ' + res.status + ')',
                    superseded: res.status === 409 && code === 'superseded',
                });
                const retryable = res.status >= 500 && res.status !== 504
                    && code !== 'ffmpeg_exited' && code !== 'ffmpeg_timeout';
                if (retryable) continue;
                throw lastErr;
            }
            const data = await res.json();
            return {
                totalDuration:   data.totalDuration || 0,
                resumeSec:       data.resumeSec     || 0,
                // Server-authoritative output info (Phase 0) — describes what
                // the player ACTUALLY receives, including post-transcode
                // codec/HDR state. Drives the Info popup so re-encoded streams
                // correctly report e.g. "H.264 SDR" even when the source was
                // HEVC HDR.
                output:          data.output || null,
                directPlayUrl:   data.directPlayUrl || null,
                // Progressive remux (MKV → native fMP4). No HLS session.
                directStreamUrl: (!data.directPlayUrl && data.directStreamUrl) || null,
                sessionToken:    (!data.directPlayUrl && !data.directStreamUrl && data.token) || null,
                manifestUrl:     (!data.directPlayUrl && !data.directStreamUrl && data.manifestUrl) || null,
                // Per-profile track defaults (task 2.2; TrackDefaultsDto) —
                // null from a server without track memory.
                trackDefaults:   data.trackDefaults || null,
            };
        }
        throw lastErr;
    }

    /** Transient notice over the player (top centre, ~6 s). Text only. */
    function showToast(root, title, detail) {
        if (!root) return;
        root.querySelectorAll(':scope > .vp-toast').forEach(t => t.remove());
        const t = document.createElement('div');
        t.className = 'vp-toast';
        t.setAttribute('role', 'status');
        const a = document.createElement('div');
        a.className = 'vp-toast__title';
        a.textContent = title || '';
        t.appendChild(a);
        if (detail) {
            const b = document.createElement('div');
            b.className = 'vp-toast__detail';
            b.textContent = detail;
            t.appendChild(b);
        }
        root.appendChild(t);
        setTimeout(() => {
            t.classList.add('vp-toast--out');
            setTimeout(() => { try { t.remove(); } catch {} }, 400);
        }, 6000);
    }

    /** No stream could be started at all (first open, or a failed switch whose
     *  previous plan wouldn't come back either). Replaces the black, HUD-less
     *  overlay that used to be left behind — with no way out but a reload —
     *  by a reason + Retry + Back. Lives in the player element; teardown()
     *  removes it (entry.errorCleanup). Also the end of the fallback chain
     *  (titleKey 'error_playback': a stream that died mid-play). */
    function showStartError(elementId, el, entry, err, retry, titleKey) {
        if (entry.errorCleanup) { try { entry.errorCleanup(); } catch {} }
        const panel = document.createElement('div');
        panel.className = 'vp-error';
        panel.setAttribute('role', 'alert');
        const title = document.createElement('div');
        title.className = 'vp-error__title';
        title.textContent = t(titleKey || 'error_start');
        panel.appendChild(title);
        if (err && err.reason) {
            const d = document.createElement('div');
            d.className = 'vp-error__detail';
            d.textContent = err.reason;
            panel.appendChild(d);
        }
        const actions = document.createElement('div');
        actions.className = 'vp-error__actions';
        const mk = (cls, text, fn) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'vp-error__btn ' + cls + ' tv-focus';
            b.textContent = text;
            b.addEventListener('click', (e) => { e.stopPropagation(); fn(); });
            actions.appendChild(b);
            return b;
        };
        const back = () => entry.refRef.current?.invokeMethodAsync('ClosePlayerFromJs').catch(() => {});
        const retryBtn = mk('vp-error__btn--primary', t('error_retry'), retry);
        const backBtn  = mk('', t('back'), back);
        panel.appendChild(actions);
        el.appendChild(panel);
        // The HUD (and its key handler) never came up: Esc / remote Back
        // close, ←/→ move between the two buttons, Enter clicks natively.
        const onKey = (e) => {
            switch (e.key) {
                case 'Escape': case 'BrowserBack': case 'GoBack': case 'MediaStop':
                    e.preventDefault(); e.stopPropagation(); back(); break;
                case 'ArrowLeft': case 'ArrowRight':
                    e.preventDefault(); e.stopPropagation();
                    (document.activeElement === retryBtn ? backBtn : retryBtn).focus();
                    break;
            }
        };
        document.addEventListener('keydown', onKey, true);
        entry.errorCleanup = () => {
            document.removeEventListener('keydown', onKey, true);
            try { panel.remove(); } catch {}
            entry.errorCleanup = null;
        };
        try { retryBtn.focus(); } catch {}
    }

    // ─────────────────────────────────────────────────────────────────
    //  attach
    // ─────────────────────────────────────────────────────────────────
    /**
     * Open the player for `mediaPath` in the element `elementId` (Blazor).
     * @param {string} elementId
     * @param {object} dotnetRef
     * @param {string} mediaPath
     * @param {object} [opts]
     * @param {boolean} [opts.autoAudio]       Apply the preferred audio language
     *                                          (HLS only), see below.
     * @param {object} [opts.labels]           Localized player strings
     *                                          (LABEL_DEFAULTS keys).
     * @param {object} [opts.trackDefaults]    A TrackDefaultsDto to fall back on
     *                                          when the start answer carries none
     *                                          (task 2.2; optional).
     */
    async function attach(elementId, dotnetRef, mediaPath, opts) {
        // A fresh open: the server picks the audio from the profile's track
        // defaults (usePrefs) and reports them all for the player to apply.
        return attachStream(elementId, dotnetRef, mediaPath,
            Object.assign({}, opts, { usePrefs: true }), beginOp(elementId));
    }

    /**
     * Build a stream + player in `elementId`. Also the restart path (audio /
     * quality switch, fallback rung), which tears the current stream down
     * first and passes the plan to keep:
     * @param {number} [opts.audioTrackIndex]  Index of audio stream in the source
     *                                          (0=first).
     * @param {string} [opts.externalAudioPath] Sideload dub to mux instead.
     * @param {number} [opts.maxHeight] / [opts.maxBitrate]  Quality caps.
     * @param {number} [opts.forceResumeSec]   Override .NET-supplied resume
     *                                          position (a restart resumes
     *                                          exactly where the old stream was).
     * @param {object} [opts.prestarted]       startStream() result obtained by
     *                                          restartStream before tearing the
     *                                          old stream down — no second start.
     * @param {object} [opts.toast]            { title, detail } to show once up.
     * @param {object} [opts.restore]          captureState() of the stream being
     *                                          replaced: subtitles, aspect, pause
     *                                          and mute carry over.
     * @param {number} op                      beginOp() token of this attempt.
     */
    async function attachStream(elementId, dotnetRef, mediaPath, opts, op) {
        const el = document.getElementById(elementId);
        if (!el) {
            console.warn('animarrPlayer.attach: container not found', elementId);
            return;
        }
        if (!isLiveOp(elementId, op)) return;
        if (opts && opts.labels) LABELS = opts.labels;
        if (opts && opts.uiLang) UI_LANG = String(opts.uiLang);
        if (WIRED.has(elementId)) {
            // A restart (switchAudio / switchQuality / fallback rung) lands
            // here on purpose; anything else is a missed detach. Either way
            // only the stream goes — KEEP (session metadata) stays.
            teardown(elementId);
        }
        // A fresh open (attach): audio from the profile's track defaults, which
        // the start answer then reports (and may change the two below).
        const usePrefs = !!(opts && opts.usePrefs);
        let audioTrackIndex = (opts && Number.isFinite(opts.audioTrackIndex))
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
        let externalAudioPath = (opts && typeof opts.externalAudioPath === 'string' && opts.externalAudioPath)
            ? opts.externalAudioPath : null;
        // The current audio's own sync offset (2.8) in what this start sends:
        // a restart carries it (baseline + offset); a fresh open (usePrefs)
        // sends only the pipeline baseline — the server adds the audio's
        // offset itself and says so (trackDefaults.audioOffsetApplied).
        const audioOffsetMs = (opts && Number.isFinite(opts.audioOffsetMs)) ? opts.audioOffsetMs : 0;
        // Subtitle track s:N burned into the picture by the server (2.6), or none.
        const burnSubtitle = (opts && Number.isFinite(opts.burnSubtitle) && opts.burnSubtitle >= 0) ? opts.burnSubtitle : null;
        if (typeof window.Artplayer !== 'function' || !window.Hls) {
            // Loaded on demand (see PLAYER_LIBS) — usually already warm from
            // MediaDetail's preload(), else this is the first-use fetch.
            try { await loadPlayerLibs(); }
            catch (e) { console.error(e && e.message || e); }
            // The overlay may have closed (detach) or another attach may have
            // taken over while the libraries loaded.
            if (!el.isConnected || !isLiveOp(elementId, op) || WIRED.has(elementId)) return;
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
            // Sync offset of the current audio: what the Sync control shows /
            // the next start sends, and what this stream was started with.
            audioOffsetMs, streamAudioOffsetMs: audioOffsetMs,
            currentBurnSub: burnSubtitle,
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
            mediaPath, dotnetRef, elementId,
        };
        WIRED.set(elementId, entry);
        // Superseded (newer attach on this element) or closed (detach) — both
        // tear this entry down, which aborts `abort`; the op check also covers
        // the moments before any fetch is in flight.
        const stale = () => abort.signal.aborted || !isLiveOp(elementId, op);
        // Same plan again, fresh token — the error panel's Retry (a fresh
        // open's retry is a fresh open again: the profile picks the audio).
        const retryOpts = (resumeAt) => ({
            audioTrackIndex, externalAudioPath, maxHeight, maxBitrate,
            forceResumeSec: resumeAt, usePrefs, audioOffsetMs, burnSubtitle,
            trackDefaults: opts && opts.trackDefaults,
        });

        if (typeof window.Artplayer !== 'function') {
            console.error('animarrPlayer: Artplayer not loaded from CDN');
            showStartError(elementId, el, entry, { reason: t('error_lib') },
                () => attachStream(elementId, dotnetRef, mediaPath, retryOpts(forceResumeSec), beginOp(elementId)));
            return;
        }

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
        if (stale()) return;

        // ── 2) Audio-sync offsets (audioOffsetsFor) + client caps ─────
        const offsets = audioOffsetsFor(audioOffsetMs);
        // Usually cached already (preload / an earlier attach); a first-ever
        // probe gets a short budget, after which the start goes without.
        const caps = await clientCapsWithin(1200);
        entry.caps = caps;
        if (stale()) return;

        // ── 3) Start session ──────────────────────────────────────────
        // Kick the probe off IN PARALLEL with /api/hls/start. The probe is a
        // second ffprobe on the source and is NOT needed to start playback —
        // only to populate the audio-track / subtitle menus — so overlapping it
        // with the (longer) start wait keeps its latency off the critical path.
        const probePromise = mediaPath
            ? fetch(apiUrl('/api/probe?path=' + encodeURIComponent(mediaPath)), { signal: abort.signal })
                .then(r => (r.ok ? r.json() : null)).catch(() => null)
            : Promise.resolve(null);
        // Chapters (marks on the seek bar, titles in its preview) — never
        // awaited by the start; drawn when they and the duration are in.
        entry.chapters = [];
        const chaptersPromise = mediaPath
            ? fetch(apiUrl('/api/chapters?path=' + encodeURIComponent(mediaPath)), { signal: abort.signal })
                .then(r => (r.ok ? r.json() : [])).then(l => (Array.isArray(l) ? l : [])).catch(() => [])
            : Promise.resolve([]);
        // Which renderer each subtitle track needs + the fonts attached to the
        // video (task 2.5). Never awaited by the start — its first call per
        // file extracts the font attachments; a libass track waits for it.
        entry.subTracksReady = mediaPath
            ? fetch(apiUrl('/api/subtitle/tracks?path=' + encodeURIComponent(mediaPath)), { signal: abort.signal })
                .then(r => (r.ok ? r.json() : null))
                .then((d) => { if (d) { entry.subTracks = d.tracks || []; entry.subFonts = d.fonts || []; } return d; })
                .catch(() => null)
            : Promise.resolve(null);

        let started = (opts && opts.prestarted) || null;
        if (!started) {
            try {
                started = await startStream(mediaPath, {
                    resumeSec, offsets, caps, audioTrackIndex, maxHeight, maxBitrate, externalAudioPath, usePrefs, burnSubtitle,
                    // Fallback floor reached earlier on this player (1.7).
                    tier: (KEEP.get(elementId) || {}).tierFloor || 0,
                    fallbackMbps: (KEEP.get(elementId) || {}).fallbackMbps,
                }, abort.signal);
            } catch (err) {
                if ((err && err.name === 'AbortError') || stale()) return;
                // A newer start for this file from this device replaced it
                // before it became playable. When that newer start is ours —
                // a newer attach / restart on this element, or the player
                // re-mounted in a new element — it is the one that renders and
                // this one just goes (stale / disconnected below). When nothing
                // newer shows up here within a short grace (it was another
                // tab, or a request we never saw), don't leave an empty
                // overlay: say so, with Retry / Back.
                if (err && err.superseded) {
                    console.info('animarrPlayer: start superseded by a newer one, dropping it');
                    setTimeout(() => {
                        if (stale() || !el.isConnected || WIRED.get(elementId) !== entry || entry.adapter) return;
                        showStartError(elementId, el, entry, { reason: t('error_superseded') },
                            () => attachStream(elementId, dotnetRef, mediaPath, retryOpts(resumeSec), beginOp(elementId)));
                    }, SUPERSEDED_GRACE_MS);
                    return;
                }
                console.error('animarrPlayer: failed to start session', err);
                // No player came up — show why, with Retry + Back, instead of a
                // black overlay that only a page reload got rid of.
                showStartError(elementId, el, entry, err,
                    () => attachStream(elementId, dotnetRef, mediaPath, retryOpts(resumeSec), beginOp(elementId)));
                return;
            }
            if (stale()) {
                // Closed / superseded while the start was answering: teardown
                // already ran without this token, so stop the session here.
                stopSession(started.sessionToken);
                return;
            }
        }
        entry.sessionToken  = started.sessionToken;
        entry.totalDuration = started.totalDuration;
        entry.resumeOffset  = started.resumeSec;
        entry.output        = started.output;
        entry.tier          = tierOfPlan(started.output, started);   // fallback rung (1.7)
        const manifestUrl     = started.manifestUrl;
        const directPlayUrl   = started.directPlayUrl;
        const directStreamUrl = started.directStreamUrl;

        // ── Per-profile track defaults (task 2.2) ─────────────────────
        // Every start answer carries them (TrackDefaultsDto); MediaDetail's
        // pre-start resolve is the fallback. mediaItemId null = a file outside
        // any identified title: its choices aren't remembered (no PUT).
        const td = started.trackDefaults || (opts && opts.trackDefaults) || null;
        entry.trackDefaults = td;
        entry.mediaItemId   = (td && td.mediaItemId) || null;
        // The audio a usePrefs start went out with: what the server mapped
        // (`applied`), or — Direct Play of a file whose default is track 0 —
        // what the browser plays anyway. The menus and every later restart
        // (quality, fallback) name it explicitly from here on.
        if (usePrefs && td && td.audio) {
            const a = td.audio;
            const onRaw = a.kind === 'embedded' && !(a.index > 0) && !!(directPlayUrl || directStreamUrl);
            if (a.applied || onRaw) {
                externalAudioPath = a.kind === 'external' && a.path ? a.path : null;
                audioTrackIndex   = externalAudioPath ? 0 : Math.max(0, a.index | 0);
                entry.currentAudIdx = audioTrackIndex;
                entry.currentExternalAudioPath = externalAudioPath;
            }
            // The audio's effective sync offset: in the stream already when
            // the server added it; else (an older server, or a plan that
            // takes none) it's what the Sync control shows, applied by a
            // restart once the HUD is up where the plan can take it.
            const want = defaultAudioOffset(td);
            if (started.trackDefaults && td.audioOffsetApplied) {
                entry.audioOffsetMs = entry.streamAudioOffsetMs = want;
            } else if (started.trackDefaults && want !== audioOffsetMs) {
                entry.audioOffsetMs = want;
            }
        }
        if (!manifestUrl && !directPlayUrl && !directStreamUrl) {
            showStartError(elementId, el, entry, { reason: t('error_empty') },
                () => attachStream(elementId, dotnetRef, mediaPath, retryOpts(resumeSec), beginOp(elementId)));
            return;
        }
        if (stale()) {
            // Already torn down without knowing this token → stop it here.
            if (WIRED.get(elementId) !== entry) stopSession(entry.sessionToken);
            return;
        }

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
                        const title = (s.tags && (s.tags.title    || s.tags.TITLE))    || '';
                        const forced = !!(s.disposition && s.disposition.forced) || FORCED_NAME_RE.test(title);
                        return {
                            name: embeddedTrackLabel(s, idx, forced),
                            lang,
                            codec: s.codec_name || '',
                            url:  apiUrl('/api/subtitle?path=' + encodeURIComponent(mediaPath))
                                + '&track=' + idx + '&format=webvtt',
                            // The file's own flag only — the "Default" mode
                            // goes by exactly what the muxer set.
                            default: !!(s.disposition && s.disposition.default),
                            // Forced flag, or a "Signs" / "Надписи" track that
                            // many releases ship without setting the flag.
                            forced,
                        };
                    });

                    // Audio
                    const auds = streams.filter(s => s.codec_type === 'audio');
                    audioList = auds.map((s, idx) => ({
                        index: idx,
                        lang: (s.tags && (s.tags.language || s.tags.LANGUAGE)) || '',
                        codec: (s.codec_name || '').toUpperCase(),
                        channels: s.channels || 0,
                        label: embeddedTrackLabel(s, idx),
                    }));

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
                        // Source bitrate for the quality menu (4.4): the
                        // container's overall rate (what ffprobe reports for
                        // every format, MKV included), else the video's own.
                        const bps = parseInt((data.format && data.format.bit_rate) || v.bit_rate
                            || (v.tags && (v.tags.BPS || v.tags['BPS-eng'])) || '0', 10) || 0;
                        mediaInfo = {
                            videoCodec: v.codec_name || '',
                            width:      v.width || 0,
                            height:     v.height || 0,
                            bitrate:    bps,
                            bitDepth:   is10bit ? 10 : 8,
                            hdr:        hdrFormats[0] || 'sdr',
                            hdrFormats,
                            audioCodec:    a ? (a.codec_name || '') : '',
                            audioChannels: a ? (a.channels || 0) : 0,
                            audioLang:     audioList[0]?.lang || '',
                            playbackTier:  directPlayUrl ? 'directplay' : (directStreamUrl ? 'directstream' : 'hls'),
                        };
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
        // Initial embedded subtitle track (-1 = none).
        let initialSubIdx = -1;
        // A restart carries the replaced stream's state over (captureState):
        // its subtitle choice instead of the account default, and no
        // autoplay / unmute behind the user's back.
        const restore = (opts && opts.restore) || null;
        const startPaused = !!(restore && restore.paused);
        const restoredExtSub = (restore && restore.sub && restore.sub.ext)
            ? (externalSubList.find(s => s.path === restore.sub.ext) || null) : null;
        // A fresh open whose subtitle mode picked a sidecar file.
        let pickedExtSub = null;

        // Closed / superseded during the probe or the external-track fetch.
        if (stale()) return;

        // Initial subtitle track: what the profile's track defaults resolved
        // (title memory → subtitle mode / language → flags, server side),
        // else — a server without track memory — the account's subtitle mode
        // applied here (pickDefaultSubtitle). Pure client-side overlay — never
        // influences the video/audio pipeline. An embedded pick goes in as
        // Artplayer's initial track; a sidecar one is switched to right after
        // creation (as on a restart).
        const tdSub = (td && td.subtitle) || null;
        if (!(restore && restore.sub) && tdSub) {
            if (tdSub.kind === 'embedded' && tdSub.index >= 0 && tdSub.index < subtitleList.length)
                initialSubIdx = tdSub.index;
            else if (tdSub.kind === 'external' && tdSub.path)
                pickedExtSub = externalSubList.find(s => samePath(s.path, tdSub.path)) || null;
            // 'off' → none
        } else if (!(restore && restore.sub)) {
            const playingAudio = externalAudioPath
                ? normLang((externalAudioList.find(x => x.path === externalAudioPath) || {}).language)
                : normLang((audioList.find(a => a.index === audioTrackIndex) || {}).lang);
            const pick = pickDefaultSubtitle(subtitleList, externalSubList, playingAudio);
            if (pick && pick.idx != null) initialSubIdx = pick.idx;
            else if (pick && pick.ext) pickedExtSub = externalSubList.find(s => s.path === pick.ext) || null;
        } else if (restore.sub.idx != null && restore.sub.idx < subtitleList.length) {
            initialSubIdx = restore.sub.idx;
        }   // else: was Off, or an external track (restoredExtSub, below)
        // hls.js buffer sizes (bufferProfile) — known up front so Info
        // can show them from the first paint.
        entry.bufferProfile = isHls ? bufferProfile() : null;
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
                // Buffer sizing (task 4.5, B23): see bufferProfile — a
                // forward buffer that rides out segment-delivery /
                // on-the-fly transcode hiccups without, on a stream-copy
                // plan (segments ready as fast as the disk reads), pulling
                // ten minutes ahead over a phone's mobile data.
                const buf = entry.bufferProfile || bufferProfile();
                const hls = new window.Hls({
                    fragLoadingTimeOut:     60000,
                    fragLoadingMaxRetry:    8,
                    manifestLoadingTimeOut: 30000,
                    levelLoadingTimeOut:    30000,
                    maxBufferHole:          4,
                    maxFragLookUpTolerance: 2,
                    nudgeOffset:            0.5,
                    nudgeMaxRetry:          10,
                    maxBufferLength:        buf.ahead,
                    maxMaxBufferLength:     buf.aheadMax,
                    maxBufferSize:          buf.bytes,
                    // Already-played media kept for instant small back-seeks.
                    backBufferLength:       buf.back,
                });
                hls.loadSource(url);
                hls.attachMedia(video);
                art.hls = hls;
                // The network can change under a running stream (Wi-Fi →
                // mobile data, Data Saver toggled): hls.js reads these per
                // fragment, so just retune them.
                const conn = navigator.connection;
                const onConn = () => {
                    const b = bufferProfile();
                    entry.bufferProfile = b;
                    Object.assign(hls.config, { maxBufferLength: b.ahead, maxMaxBufferLength: b.aheadMax,
                                                maxBufferSize: b.bytes, backBufferLength: b.back });
                };
                try { conn && conn.addEventListener && conn.addEventListener('change', onConn); } catch {}
                art.on('destroy', () => {
                    try { conn && conn.removeEventListener && conn.removeEventListener('change', onConn); } catch {}
                    try { hls.destroy(); } catch {}
                });
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
                    if (startPaused) return;   // restart of a paused stream
                    const p = video.play();
                    if (p && typeof p.catch === 'function') p.catch(() => {});
                });
                // Fatal hls.js errors used to be logged and left as a
                // frozen / black player. A media (decode / MSE) error
                // gets hls.js's own recovery once; then — or for any
                // other fatal error — the fallback chain takes over
                // (handleStreamFailure: network → same rung again,
                // anything else → one rung down).
                let mediaRecoveryUsed = false;
                hls.on(window.Hls.Events.ERROR, (evt, data) => {
                    if (!data.fatal) return;
                    console.error('animarr hls fatal:', data.type, data.details);
                    const T = window.Hls.ErrorTypes || {};
                    if (data.type === T.MEDIA_ERROR && !mediaRecoveryUsed) {
                        mediaRecoveryUsed = true;
                        try { hls.recoverMediaError(); return; } catch {}
                    }
                    // The LAST fragment failing to load is the stream's end,
                    // not a broken stream: a stream copy of a file whose GOPs
                    // run longer than a segment (until the server cuts copy
                    // segments on keyframes) can announce a tail it never
                    // serves. Play out what is buffered, then end as usual
                    // (Up Next / autoplay) instead of stepping down a rung.
                    const frag = data.frag;
                    const mediaEnd = (video && Number.isFinite(video.duration) && video.duration) || 0;
                    if (/^fragLoad/.test(data.details || '') && frag && mediaEnd > 0
                        && frag.start + frag.duration >= mediaEnd - 1.5) {
                        console.info('animarrPlayer: the tail segment is missing, ending there');
                        try { hls.stopLoad(); } catch {}
                        endAtBufferedTail(video);
                        return;
                    }
                    handleStreamFailure(elementId, entry,
                        data.type === T.NETWORK_ERROR ? 'network' : 'decode',
                        'hls.js: ' + (data.details || data.type));
                });
            },
        } : {},
        title: mediaPath.split(/[\/\\]/).pop(),
        autoplay: !startPaused,
        playsInline: true,
        // Restore the user's last-set volume across sessions. Saved by the
        // volume popup; defaults to 1.0 on first run.
        volume: readVolumePref(),
        // Mute survives a restart (it isn't persisted across opens).
        muted: !!(restore && restore.muted),
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
        // undefined (i.e. media with NO subtitle tracks). Without an initial
        // track (none in the file, or subtitles Off) it carries no url, so
        // nothing loads until the CC menu picks one; escape + style stay so
        // that later switch() (which merges this object) inherits them.
        // No url: the initial track goes up through applySubtitleChoice
        // right after creation, like every later one (it may need libass).
        subtitle: {
            type: 'vtt',          // 'webvtt' is not a value Artplayer knows
            encoding: 'utf-8',
            // Artplayer paints cues with innerHTML: escape:true keeps file
            // text inert, and the 'subtitleAfterUpdate' hook below repaints
            // them through renderCuesSafely so <b>/<i>/<u> still render.
            escape: true,
            // No inline style: size / colour / edge / position come from
            // applySubStyle's CSS variables (an inline font-size would
            // pin the size against the player's size step).
        },
        layers: [{
            name: 'animarr-hud',
            html: buildHudHtml(stylePref),
            style: { position: 'absolute', inset: '0', zIndex: '30',
                     pointerEvents: 'none' },
        }],
        });
        // Repaint every cue change through the safe renderer (see
        // renderCuesSafely). Artplayer emits this synchronously right after
        // its own (escaped) innerHTML write, so nothing paints in between.
        art.on('subtitleAfterUpdate', (cues) => {
            try { renderCuesSafely(art.template.$subtitle, cues); }
            catch (e) { console.warn('subtitle render failed', e); }
        });
        // A freshly loaded track starts unshifted: put the offset back.
        art.on('subtitleLoad', () => applySubtitleOffset(entry));
        applySubStyle(art.template.$player);
        adapter = new ArtplayerAdapter(art);
        // Saved volume, set on the element itself: Artplayer skips a
        // `volume: 0` option (falsy) and then applies its own stored level,
        // which it never updates for 0 — a muted-by-volume player came back
        // loud. Directly on <video>, so no Artplayer notice / storage write.
        try { art.video.volume = readVolumePref(); } catch {}
        // Playback speed (remembered): defaultPlaybackRate too, so a source
        // reload (Direct Stream seek, new src) keeps it.
        applyRate(art.video, readRatePref());
        // Direct Stream: route seeks through a remux-reload (the source has
        // no Range) and read duration off the server total, not the live
        // stream (whose video.duration is Infinity).
        if (isDirectStream) adapter.enableDirectStream(directStreamBase, entry.totalDuration);

        entry.art = art;
        entry.adapter = adapter;

        // ── Stream health → fallback chain (task 1.7) ─────────────────
        // hls.js fatal errors report from the customType hook above; the
        // <video> element covers Direct Play / Direct Stream (and MSE errors
        // hls.js didn't see first). MEDIA_ERR_ABORTED is ours (a src swap).
        adapter.on('play',  () => { entry.userPaused = false; });
        adapter.on('pause', () => { entry.userPaused = true; });
        const vHealth = adapter.rawVideoElement && adapter.rawVideoElement();
        if (vHealth) {
            vHealth.addEventListener('error', () => {
                const err = vHealth.error;
                if (!err || err.code === 1) return;
                handleStreamFailure(elementId, entry, err.code === 2 ? 'network' : 'decode',
                    'MediaError ' + err.code + (err.message ? ': ' + err.message : ''));
            });
            // A file whose video codec the browser can't decode often plays its
            // audio over a black frame without any error: catch videoWidth 0.
            const expectVideo = !!(entry.output && entry.output.videoCodec);
            if (expectVideo) {
                vHealth.addEventListener('loadeddata', () => {
                    setTimeout(() => {
                        if (vHealth.videoWidth === 0 && vHealth.readyState >= 2)
                            handleStreamFailure(elementId, entry, 'decode', 'video track did not decode');
                    }, 1500);
                }, { once: true });
            }
        }

        // Subtitle offset: carried over by a restart, else the title's
        // remembered one (track defaults), else none.
        entry.subOffset = (restore && Number.isFinite(restore.subOffset)) ? restore.subOffset
            : (td && td.subtitleOffsetMs) ? Math.max(-SUB_OFFSET_MAX, Math.min(SUB_OFFSET_MAX, td.subtitleOffsetMs / 1000))
            : 0;
        // The initial subtitle — an embedded track, a sidecar (restored by a
        // restart or picked by the defaults) or Off — through the one place
        // that picks its renderer. What the CC menu marks active follows.
        const extSubNow = restoredExtSub || pickedExtSub;
        applySubtitleChoice(entry, initialSubIdx >= 0 ? { idx: initialSubIdx }
            : extSubNow ? { ext: extSubNow.path } : null);

        // Picture: Fit unless a restart carries the replaced stream's
        // mode / zoom / pan (and whether the user picked it) over, or the
        // title remembers one (track defaults) — a remembered picture is the
        // user's own choice, so no auto-crop over it either.
        const rememberedView = !restore && td && td.aspect ? decodeView(td.aspect) : null;
        entry.currentAspect = (restore && restore.aspect) || (rememberedView && rememberedView.aspect) || 'fit';
        entry.aspectPicked  = !!(restore && restore.aspectPicked) || !!rememberedView;
        entry.zoom          = (restore && restore.zoom) || (rememberedView && rememberedView.zoom) || 1;
        entry.pan           = (restore && restore.pan) || (rememberedView && rememberedView.pan) || 0;
        if (entry.currentAspect !== 'fit' || entry.zoom !== 1 || entry.pan !== 0) {
            applyView(entry);
            // Again once the stream is up (Artplayer sizes the ratio box
            // from the player, which a fresh instance may still be laying out).
            adapter.once('loadedmetadata', () => applyView(entry));
        }

        // ── Preferred audio language — servers WITHOUT track memory only ────
        // A server with it picks the profile's audio itself on the first
        // start (usePrefs) and answers trackDefaults; this second start
        // (B11) is only the fallback for an older server (the phone app can
        // talk to one). HLS only: Direct Play / Direct Stream serve the raw
        // container and are never sacrificed for an audio-language
        // preference. Once per user-initiated open (opts.autoAudio).
        if (opts && opts.autoAudio && !started.trackDefaults && entry.sessionToken && PREFS.audioLang
            && Array.isArray(audioList) && audioList.length > 1) {
            const want = audioList.findIndex(a => normLang(a.lang) === PREFS.audioLang);
            if (want > 0 && want !== entry.currentAudIdx) {
                // Defer past the rest of attach()'s wiring (autostart + HUD)
                // before we tear the session down and restart with the preferred
                // audio map. Only reached on a transcoding HLS file whose default
                // audio isn't the preferred language, so the extra warm-up is
                // rare; guard against the user navigating away meanwhile.
                setTimeout(() => {
                    if (WIRED.get(elementId) === entry) {
                        try { switchAudioTrack(elementId, want); } catch {}
                    }
                }, 600);
            }
        }

        // Autostart reliability (covers BOTH the HLS and Direct-Play paths).
        // Artplayer's autoplay can lose the race against media readiness, so we
        // also kick play() on the first `canplay` of the underlying <video>.
        // One-shot; harmless if playback already started. Rejections (autoplay
        // policy) are swallowed — the play button is the fallback. Skipped
        // when restarting a stream the user had paused.
        try {
            const vEl = adapter.rawVideoElement && adapter.rawVideoElement();
            if (vEl && !vEl.__animarrAutostart && !startPaused) {
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
        // Root element for HUD events: Artplayer's player wrapper (the HUD
        // layer lives inside it).
        const hudRoot = art.template.$player || art.container;
        const hudCtl = attachHud(adapter, hudRoot, entry, {
            back:  () => refRef.current?.invokeMethodAsync('ClosePlayerFromJs').catch(() => {}),
            prev:  () => refRef.current?.invokeMethodAsync('InvokePrev').catch(() => {}),
            next:  () => refRef.current?.invokeMethodAsync('InvokeNext').catch(() => {}),
            cast:  () => openCastPopup(hudRoot, 'cast', mediaPath, adapter),
            volume: () => openVolumePopup(hudRoot, 'volume', adapter, (v) => {
                hudCtl?.setVolumeIcon(v, adapter.muted);
            }),
            aspect: () => openPictureSettingsPopup(hudRoot, 'aspect', entry),
            // Pinch on touch (attachHud): zoom + vertical pan, live.
            pinch: (zoom, pan) => {
                entry.zoom = zoom;
                entry.pan  = pan;
                applyView(entry);
                queueTrackPrefs(elementId, { aspect: encodeView(entry) });
            },
            getView: () => ({ zoom: entry.zoom || 1, pan: entry.pan || 0 }),
            offset: () => {
                if (!offsetChannel) return;
                const cur = buildAudioOptions(entry).find(o => o.current);
                openAudioSyncPopup(hudRoot, 'offset', entry, cur ? cur.label : '',
                    (ms) => setAudioOffset(elementId, ms));
            },
            audio: () => {
                const aopts = buildAudioOptions(entry);
                if (aopts.length === 0) return;
                const curIdx = Math.max(0, aopts.findIndex(o => o.current));
                openPickerPopup(hudRoot, 'audio', t('hud_audio'),
                    aopts.map(o => o.label), curIdx,
                    (i) => {
                        const o = aopts[i];
                        if (!o || o.current) return;
                        // No live switching with our single-stream transcode —
                        // tear the session down and restart, mapping either the
                        // chosen in-file stream (`-map 0:a:N`) or the external
                        // dub file (`-map 1:a:0`), carrying the position over.
                        // Remembered for the title, with its own sync offset
                        // (chooseAudio; a failed switch puts the previous
                        // audio back into the memory).
                        chooseAudio(elementId, {
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
                // Last row: the timing / style panel (only with a player
                // that renders the cues itself, i.e. Artplayer).
                // Embedded bitmap tracks also get an explicit "burn in" row
                // (2.6) after the list; one the player can't draw at all is
                // labelled so in its own row.
                const burnRows = emb.map((s, idx) => ({ idx, plan: subtitlePlan(entry, { idx }) }))
                    .filter(r => r.plan && (r.plan.kind === 'bitmap' || r.plan.kind === 'burn'));
                const settingsIdx = entry.art ? 1 + emb.length + ext.length + burnRows.length : -1;
                const items = [t('sub_off'),
                    ...emb.map((s, idx) => {
                        const pl = burnRows.find(r => r.idx === idx);
                        return pl && pl.plan.kind === 'burn' ? s.name + ' · ' + t('sub_burn_in') : s.name;
                    }),
                    ...ext.map(s => externalTrackLabel(s)),
                    ...burnRows.map(r => ({ label: t('sub_burn_in') + ': ' + emb[r.idx].name, cls: 'vp-hud-popup__item--more' })),
                    ...(settingsIdx > 0 ? [{ label: t('sub_settings') + '…', cls: 'vp-hud-popup__item--more' }] : [])];
                let cur = 0;
                if (entry.currentBurnSub != null) {
                    const bi = burnRows.findIndex(r => r.idx === entry.currentBurnSub);
                    if (bi >= 0) cur = 1 + emb.length + ext.length + bi;
                } else if (entry.currentExtSubPath != null) {
                    const ei = ext.findIndex(s => s.path === entry.currentExtSubPath);
                    if (ei >= 0) cur = 1 + emb.length + ei;
                } else if (entry.currentSubIdx != null) {
                    cur = entry.currentSubIdx + 1;
                }
                openPickerPopup(hudRoot, 'cc', t('subtitles'), items, cur, (i) => {
                    const burnAt = i - (1 + emb.length + ext.length);
                    if (burnAt >= 0 && burnAt < burnRows.length) {
                        burnSubtitleIn(elementId, burnRows[burnAt].idx);
                        return;
                    }
                    // Any other pick while a track is burned in: restart without it.
                    if (entry.currentBurnSub != null && i !== settingsIdx) {
                        const idx = i - 1;
                        const choice = i === 0 ? null : idx < emb.length ? { idx } : { ext: ext[idx - emb.length].path };
                        unburnSubtitle(elementId, choice);
                        queueTrackPrefs(elementId, { subtitle: !choice ? { kind: 'off' }
                            : choice.idx != null ? { kind: 'embedded', index: choice.idx }
                            : { kind: 'external', path: choice.ext } });
                        return;
                    }
                    if (i === settingsIdx) {
                        // After this picker has closed (it closes right after
                        // onPick; opening now would just toggle it shut).
                        setTimeout(() => openSubtitleSettingsPopup(hudRoot, 'cc', entry, entry.art.template.$player), 0);
                        return;
                    }
                    // Each pick is remembered for the title (2.2): embedded by
                    // its s:N, a sidecar by its path, or Off.
                    if (i === 0) {
                        applySubtitleChoice(entry, null);
                        queueTrackPrefs(elementId, { subtitle: { kind: 'off' } });
                        return;
                    }
                    const idx = i - 1;
                    if (idx < emb.length) {
                        applySubtitleChoice(entry, { idx }, true);
                        queueTrackPrefs(elementId, { subtitle: { kind: 'embedded', index: idx } });
                    } else {
                        const s = ext[idx - emb.length];
                        applySubtitleChoice(entry, { ext: s.path }, true);
                        queueTrackPrefs(elementId, { subtitle: { kind: 'external', path: s.path } });
                    }
                });
            },
            quality: () => {
                // One list, three kinds of entries (single-select picker;
                // plan 4.4 — no "Auto", a choice lasts for this file only):
                //   • Original · {h}p · {source bitrate} — the file's own
                //     stream (Direct Play / remux / copy; the server only
                //     re-encodes it when this client can't decode it).
                //   • Resolution rungs below the source — downscale +
                //     re-encode at the server's ladder bitrate ("~N Mbps").
                //   • Bitrate caps "≤ N Mbps" at the SOURCE resolution, only
                //     below the source's own bitrate (a cap above it would
                //     just re-encode for nothing).
                const srcH = (entry.mediaInfo && entry.mediaInfo.height)
                          || (entry.output && entry.output.height) || 0;
                const srcMbps = ((entry.mediaInfo && entry.mediaInfo.bitrate) || 0) / 1e6;
                const mbps = t('unit_mbps');
                const items = [{
                    label: [t('quality_original'), srcH ? srcH + 'p' : '', srcMbps ? fmtMbps(srcMbps) + ' ' + mbps : '']
                        .filter(Boolean).join(' · '),
                    h: 0, b: 0,
                }];
                QUALITY_HEIGHTS.forEach(r => {
                    if (srcH === 0 ? r <= 1080 : r < srcH)
                        items.push({ label: r + 'p · ~' + fmtMbps(autoMbpsFor(r)) + ' ' + mbps, h: r, b: 0 });
                });
                // Clearly below the source: its rate is the container's (audio
                // included), so a cap within ~10 % of it saves next to nothing.
                // Unknown source bitrate: fall back to a ceiling by resolution.
                const brCeil = srcMbps > 0 ? srcMbps * 0.9
                    : srcH >= 2160 ? 81 : srcH >= 1440 ? 41 : srcH >= 1080 ? 21 : srcH >= 720 ? 11 : 9;
                QUALITY_CAPS.forEach(b => {
                    if (b < brCeil)
                        items.push({ label: '≤ ' + b + ' ' + mbps + (srcH ? ' · ' + srcH + 'p' : ''), h: 0, b: b });
                });
                const curH = entry.currentMaxHeight || 0;
                const curB = entry.currentMaxBitrate || 0;
                let curIdx = items.findIndex(o => o.h === curH && o.b === curB);
                if (curIdx < 0) curIdx = 0;
                openPickerPopup(hudRoot, 'quality', t('quality'), items.map(o => o.label), curIdx, (i) => {
                    const it = items[i];
                    if (it.h === (entry.currentMaxHeight || 0) && it.b === (entry.currentMaxBitrate || 0)) return;
                    switchQuality(elementId, it.h, it.b);
                });
            },
            info: () => openInfoPopup(hudRoot, 'info', entry.infoLines || []),
            speed: () => {
                const cur = readRatePref();
                openPickerPopup(hudRoot, 'speed', t('hud_speed'),
                    SPEEDS.map(r => r === 1 ? t('speed_normal') : r + '×'), SPEEDS.indexOf(cur),
                    (i) => setRate(SPEEDS[i]));
            },
            speedStep: (dir) => {
                const i = SPEEDS.indexOf(readRatePref());
                setRate(SPEEDS[Math.max(0, Math.min(SPEEDS.length - 1, (i < 0 ? 2 : i) + dir))]);
            },
            fullscreen: () => toggleFullscreen(adapter),
        });
        entry.hud = hudCtl;
        // Playback speed: remembered across opens (readRatePref), applied
        // to the element, shown on the Speed button.
        function setRate(r) {
            saveRatePref(r);
            applyRate(art.video, r);
            hudCtl.setSpeed(r);
        }
        hudCtl.setSpeed(readRatePref());
        entry.hudRoot = hudRoot;

        // Loudness normalisation for this stream's audio (profile setting).
        setupLoudness(entry);

        // Chapter marks: once both the list and the duration are known.
        chaptersPromise.then((list) => {
            if (WIRED.get(elementId) !== entry) return;
            entry.chapters = list;
            hudCtl.setChapters(list);
            adapter.on('durationchange', () => hudCtl.setChapters(entry.chapters));
        });

        // Hide buttons that don't apply to current playback.
        hudCtl.setOffsetEnabled(!!offsetChannel);
        // A remembered sync offset the start didn't carry (see the track
        // defaults above): apply it now, seamlessly.
        if (offsetChannel && (entry.audioOffsetMs || 0) !== (entry.streamAudioOffsetMs || 0)) {
            setTimeout(() => { if (WIRED.get(elementId) === entry) applyAudioOffset(elementId); }, 600);
        }
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
                if (o.height) lines.push(t('info_resolution') + ': ' + o.height + 'p');
                const vparts = [];
                if (o.videoCodec) vparts.push(o.videoCodec.toUpperCase());
                if (o.bitDepth >= 10) vparts.push('10-bit');
                if (vparts.length) lines.push(t('info_video') + ': ' + vparts.join(' '));
                // The Info block reports what's actually ON SCREEN, not the
                // source's flags. No browser renders Dolby Vision — the web
                // player plays the HDR10/HLG base layer — so drop the DV tag.
                const hdrFormats = (o.hdrFormats || []).filter(f => f !== 'dolbyvision');
                const hdrs = hdrFormats.map(fmt =>
                    fmt === 'dolbyvision' ? 'Dolby Vision'
                  : fmt === 'hdr10' ? 'HDR10'
                  : fmt === 'hlg' ? 'HLG' : fmt.toUpperCase());
                if (hdrs.length) lines.push('HDR: ' + hdrs.join(', '));
                // The audio the player receives: the server reports the
                // SELECTED track (an external dub included — it probes the
                // mapped file), so one line covers it; for a dub only the part
                // the codec / language can't say is added (which dub). This
                // used to need a separate "Dub:" line, when audio* still
                // described the source's first track.
                const exo = entry.currentExternalAudioPath
                    ? (entry.externalAudioList || []).find(x => x.path === entry.currentExternalAudioPath) : null;
                const aParts = [];
                const aTech = [codecName(o.audioCodec), channelLabel(o.audioChannels)].filter(Boolean).join(' ');
                if (aTech) aParts.push(aTech);
                const aLang = languageName(o.audioLanguage || (exo && exo.language) || '');
                if (aLang) aParts.push(aLang);
                if (entry.currentExternalAudioPath) {
                    const core = exo ? externalLabelCore(exo) : '';
                    aParts.push((core ? core + ' · ' : '') + t('track_external'));
                }
                if (aParts.length) lines.push(t('info_audio') + ': ' + aParts.join(' · '));
                // Loudness normalisation in force (stage 7).
                if (entry.loudness)
                    lines.push(tf('info_loudness', (Math.round(entry.loudness.lufs * 10) / 10).toFixed(1),
                        (entry.loudness.gainDb > 0 ? '+' : '') + (Math.round(entry.loudness.gainDb * 10) / 10).toFixed(1)));
                // The audio's own sync offset (2.8), when it has one.
                if (entry.streamAudioOffsetMs)
                    lines.push(t('hud_sync') + ': ' + (entry.streamAudioOffsetMs > 0 ? '+' : '−')
                        + Math.abs(entry.streamAudioOffsetMs) + ' ' + t('unit_ms'));
                // What was chosen in the Quality menu, then how the server
                // delivers it and why (plan 4.2 — the same on the button).
                lines.push(t('quality') + ': ' + qualityChoiceText(entry));
                lines.push(t('info_playback') + ': ' + planName(o.plan));
                const why = reasonText(o);
                if (why) lines.push(t('info_reason') + ': ' + why);
            }
            // hls.js forward / back buffer (only HLS has one we size).
            const bp = entry.bufferProfile;
            if (bp && isHls) {
                lines.push(tf('info_buffer', bp.ahead, bp.back)
                    + (bp.kind !== 'default' ? ' (' + t('buffer_' + bp.kind) + ')' : ''));
            }
            // Quality button: the quality in force as a badge, amber when the
            // video is re-encoded; the tooltip names the plan and reason.
            hudCtl.setQuality(qualityBadge(entry), isVideoReencode(o),
                t('quality') + ': ' + qualityChoiceText(entry)
                + (o ? ' — ' + planName(o.plan) + (reasonText(o) ? ' (' + reasonText(o) + ')' : '') : ''));
            // What this browser reported (debugging Direct Play decisions).
            lines.push(describeCaps(entry.caps));
            entry.infoLines = lines;
            // If the Info popup is open, refresh its rows live (the 30s clock
            // tick also re-runs this).
            const openInfo = hudRoot.querySelector('.vp-hud-popup--info .vp-hud-popup__list');
            if (openInfo) {
                openInfo.innerHTML = (lines.length ? lines : [t('info_none')])
                    .map(l => `<div class="vp-hud-popup__info">${escapeHtml(l)}</div>`).join('');
            }
        }
        updateMeta();
        entry.updateMeta = updateMeta;
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
                            playbackRate: (art.video && art.video.playbackRate) || 1,
                            position:     Math.min(adapter.currentTime, dur),
                        });
                    } catch {}
                });
                adapter.on('play',  () => { ms.playbackState = 'playing'; });
                adapter.on('pause', () => { ms.playbackState = 'paused';  });
            }
        } catch (e) { console.warn('mediaSession wire-up failed', e); }

        // A restart of an already-open player (audio / quality switch, a
        // fallback): Blazor pushed setMediaSession once, after the FIRST attach,
        // and won't again — re-apply what it sent (no-op on a first attach,
        // where KEEP holds nothing yet and Blazor's call follows).
        applyMediaSession(elementId);
        // restartStream's failure path re-opened the previous plan: say why.
        if (opts && opts.toast) showToast(hudRoot, opts.toast.title, opts.toast.detail);

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

        // Black bars baked into the frame (3.3): measured by the server
        // (/api/media-facts — cropdetect at scan / first ask; "pending" is
        // asked for again once). Feeds "Remove black bars" in the Picture
        // panel and the automatic cut (autoRemoveBars), re-judged when the
        // player's shape changes (rotation, window resize).
        (async () => {
            for (let attempt = 0; attempt < 2; attempt++) {
                let facts = null;
                try {
                    const r = await fetch(apiUrl('/api/media-facts?path=' + encodeURIComponent(mediaPath)
                        + '&wait=' + (attempt === 0 ? 2 : 10)), { signal: abort.signal });
                    facts = r.ok ? await r.json() : null;
                } catch { return; }
                if (WIRED.get(elementId) !== entry) return;
                entry.facts = facts;
                const status = facts && facts.video && facts.video.cropStatus;
                if (status !== 'pending') break;
            }
            if (WIRED.get(elementId) !== entry) return;
            if (entry.currentAspect === BARS_KEY) applyView(entry);   // a remembered / carried "remove bars"
            autoRemoveBars(entry);
        })();
        let barsResizeTimer = null;
        const onBarsResize = () => {
            clearTimeout(barsResizeTimer);
            barsResizeTimer = setTimeout(() => { if (WIRED.get(elementId) === entry) autoRemoveBars(entry); }, 300);
        };
        window.addEventListener('resize', onBarsResize);
        entry.offResize = () => { clearTimeout(barsResizeTimer); window.removeEventListener('resize', onBarsResize); };

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

            const wn = (KEEP.get(elementId) || {}).watchNext;
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

    /** Player closed (or Blazor moving on to another episode): drop the
     *  stream AND everything kept for restarts of this element. */
    function detach(elementId) {
        // A choice made in the last second still goes to the track memory.
        flushTrackPrefs(elementId);
        // Any start still in flight for this element is now stale (B27).
        LIVE_OP.delete(elementId);
        teardown(elementId);
        KEEP.delete(elementId);
    }

    /** Stop the current stream on this element — HLS session DELETE, timers,
     *  HUD, Artplayer — but keep KEEP's session metadata so a
     *  restart on the same element (attach again) can re-apply it. */
    function teardown(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry) return;
        entry.refRef.current = null;
        // A switch still warming up its new stream: abort that start (the
        // server then stops the ffmpeg it was spawning).
        if (entry.pendingSwitch) { try { entry.pendingSwitch.abort(); } catch {} entry.pendingSwitch = null; }
        if (entry.errorCleanup) { try { entry.errorCleanup(); } catch {} }
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
        if (entry.offResize) { try { entry.offResize(); } catch {} entry.offResize = null; }
        entry.subToken = (entry.subToken || 0) + 1;   // a libass start still loading backs out
        disposeSubRenderer(entry);
        disposeLoudness(entry);
        if (entry.adapter) {
            entry.adapter.destroy();
            entry.adapter = null;
        }
        entry.art = null;
        document.body.classList.remove('player-open');
        WIRED.delete(elementId);
    }

    /**
     * A stream that was playing died (task 1.7): the <video> raised a
     * MediaError, hls.js hit a fatal error it couldn't recover, or the video
     * track never decoded (videoWidth stays 0 — Chrome plays the audio of a
     * file whose video codec it can't decode and raises nothing).
     *   • 'network' — restart the same rung at the same spot (the session was
     *     likely reaped / the server restarted); at most twice per player.
     *   • 'decode'  — step one rung down the fallback ladder (Direct Play →
     *     Direct Stream → HLS copy → transcode); the floor only ever rises, so
     *     it can't loop. Below transcode there is nothing left: the error
     *     panel (Retry / Back).
     * Position, pause, mute, subtitles and aspect carry over (captureState).
     */
    function handleStreamFailure(elementId, entry, kind, detail) {
        if (entry.failed || entry.abort.signal.aborted || WIRED.get(elementId) !== entry || !entry.adapter) return;
        entry.failed = true;
        const kept = keepFor(elementId);
        const rung = Math.max(entry.tier || 0, kept.tierFloor || 0);
        console.warn('animarrPlayer: stream failed (' + kind + ', rung ' + rung + '): ' + (detail || ''));
        let toastKey;
        if (kind === 'network' && (kept.netRestarts || 0) < 2) {
            kept.netRestarts = (kept.netRestarts || 0) + 1;
            toastKey = 'fallback_restarted';
        } else if (rung < FALLBACK_TRANSCODE) {
            kept.tierFloor = rung + 1;
            const h = (entry.mediaInfo && entry.mediaInfo.height) || (entry.output && entry.output.height) || 1080;
            kept.fallbackMbps = Math.max(2, Math.round(autoMbpsFor(h)));   // the server takes whole Mbps
            toastKey = 'fallback_switched';
        } else {
            try { entry.adapter.pause(); } catch {}
            const el = document.getElementById(elementId);
            if (el) showStartError(elementId, el, entry, { reason: detail || '' },
                () => attachStream(elementId, entry.dotnetRef, entry.mediaPath,
                    Object.assign({}, currentPlan(entry), { forceResumeSec: entry.adapter ? entry.adapter.currentTime : 0 }),
                    beginOp(elementId)),
                'error_playback');
            return;
        }
        const snap = captureState(entry);
        // Paused only if the USER paused — a stalled / errored element's own
        // state says nothing about what they wanted.
        snap.paused = !!entry.userPaused;
        attachStream(elementId, entry.dotnetRef, entry.mediaPath,
            Object.assign({}, currentPlan(entry), {
                forceResumeSec: snap.pos, restore: snap,
                toast: { title: t(toastKey), detail: detail || '' },
            }), beginOp(elementId));
    }

    /** "~N Mbps" the server's re-encode ladder uses for an output height —
     *  mirrors RateForHeight in HlsSessionService.Playlist.cs. */
    function autoMbpsFor(h) {
        return h <= 480 ? 2.5 : h <= 576 ? 3.5 : h <= 720 ? 6 : h <= 1080 ? 12 : h <= 1440 ? 24 : 40;
    }
    // Quality menu rungs (plan 4.4): resolutions offered below the source…
    const QUALITY_HEIGHTS = [2160, 1440, 1080, 720, 576, 480, 360];
    // …and bitrate caps (Mbps) offered below the source's own bitrate.
    const QUALITY_CAPS = [2, 4, 6, 8, 10, 15, 20, 30, 40, 60, 80];
    /** 2.5 → "2.5", 12 → "12", 8.43 → "8.4". */
    function fmtMbps(v) {
        return v >= 10 ? String(Math.round(v)) : String(Math.round(v * 10) / 10);
    }
    /** Short text for the quality button's badge: the cap in force, else
     *  the height the player receives ("4K" from 2160 up). */
    function qualityBadge(entry) {
        if (entry.currentMaxHeight > 0) return entry.currentMaxHeight + 'p';
        if (entry.currentMaxBitrate > 0) return '≤' + entry.currentMaxBitrate + 'M';
        const h = (entry.output && entry.output.height) || (entry.mediaInfo && entry.mediaInfo.height) || 0;
        return h >= 2160 ? '4K' : h > 0 ? h + 'p' : '';
    }
    /** "Original · 1080p" / "720p" / "≤ 6 Mbps · 1080p" — what was chosen. */
    function qualityChoiceText(entry) {
        const srcH = (entry.mediaInfo && entry.mediaInfo.height) || (entry.output && entry.output.height) || 0;
        if (entry.currentMaxHeight > 0) return entry.currentMaxHeight + 'p · ~' + fmtMbps(autoMbpsFor(entry.currentMaxHeight)) + ' ' + t('unit_mbps');
        if (entry.currentMaxBitrate > 0) return '≤ ' + entry.currentMaxBitrate + ' ' + t('unit_mbps') + (srcH ? ' · ' + srcH + 'p' : '');
        return t('quality_original') + (srcH ? ' · ' + srcH + 'p' : '');
    }
    /** The plan's video was re-encoded (not Direct Play / remux / copy). */
    function isVideoReencode(output) {
        return !!(output && /-reencode$/.test(output.plan || ''));
    }
    // output.transcodeReasonCodes (main reason first, then side notes) →
    // the player.reason_* strings; the server's English transcodeReason is
    // the fallback for a code this player doesn't know yet.
    const REASON_CODES = ['user_quality_cap', 'user_bitrate_cap', 'codec_unsupported', 'dolby_vision', 'interlaced',
        'burned_subtitle', 'container_unsupported', 'audio_unsupported', 'audio_track', 'external_audio',
        'audio_sync', 'hdr_tonemap', 'hdr_lost'];
    /** Why the stream isn't the file as is, localized; '' for Direct Play. */
    function reasonText(output) {
        if (!output) return '';
        const codes = Array.isArray(output.transcodeReasonCodes) && output.transcodeReasonCodes.length
            ? output.transcodeReasonCodes : output.transcodeReasonCode ? [output.transcodeReasonCode] : [];
        if (codes.length && codes.every(c => REASON_CODES.includes(c)))
            return codes.map(c => t('reason_' + c)).join(' · ');
        return output.transcoded || codes.length ? (output.transcodeReason || '') : '';
    }
    /** Human name of output.plan. */
    function planName(plan) {
        return ({
            'directplay':     'Direct Play',
            'directstream':   'Direct Stream · remux',
            'ts-copy':        'HLS · TS stream-copy',
            'fmp4-copy':      'HLS · fMP4 stream-copy',
            'vaapi-reencode': 'HLS · VAAPI → H.264',
            'nvenc-reencode': 'HLS · NVENC → H.264',
            'sw-reencode':    'HLS · CPU → H.264',
        })[plan] || 'HLS';
    }

    /** The plan the current stream was started with — what a switch changes
     *  one part of, and what a failed switch falls back to. */
    function currentPlan(entry) {
        return {
            audioTrackIndex:   entry.currentAudIdx || 0,
            externalAudioPath: entry.currentExternalAudioPath || null,
            maxHeight:         entry.currentMaxHeight || 0,
            maxBitrate:        entry.currentMaxBitrate || 0,
            audioOffsetMs:     entry.audioOffsetMs || 0,
            burnSubtitle:      entry.currentBurnSub != null ? entry.currentBurnSub : null,
        };
    }

    /** Player state to carry onto the restarted stream (attachStream applies
     *  it as opts.restore). Without it a restart reset the subtitles — an
     *  external track always, an embedded one to the account default — the
     *  aspect, pause and mute. */
    function captureState(entry) {
        const a = entry.adapter;
        const v = a.rawVideoElement && a.rawVideoElement();
        return {
            pos:    a.currentTime || 0,
            // The element's own flag: adapter.playing is false while merely
            // buffering, which isn't a pause the user asked for.
            paused: v ? v.paused : !a.playing,
            muted:  !!a.muted,
            sub:    entry.currentExtSubPath ? { ext: entry.currentExtSubPath }
                  : entry.currentSubIdx != null ? { idx: entry.currentSubIdx }
                  : { off: true },
            subOffset:    entry.subOffset || 0,
            aspect:       entry.currentAspect || 'fit',
            aspectPicked: !!entry.aspectPicked,
            zoom:         entry.zoom || 1,
            pan:          entry.pan || 0,
        };
    }

    /** Is the stream we froze for a switch still servable? Direct Play /
     *  Direct Stream hold no server session, so yes. An HLS session normally
     *  is too: the server retires a client's older session on the same file
     *  only once the new one has produced its first segment, so a failed
     *  start leaves it running. Still ask (keepalive → 404) — it may have
     *  been reaped or the server restarted while the new start was warming. */
    async function previousStreamAlive(entry) {
        if (!entry.sessionToken) return true;
        try {
            const r = await fetch(apiUrl('/api/hls/keepalive?token=' + encodeURIComponent(entry.sessionToken)),
                { method: 'POST' });
            return r.ok;
        } catch { return false; }
    }

    /**
     * Restart the stream with part of the plan changed (audio track / dub,
     * quality caps). There's no live track switching in our single-stream
     * transcode, so a new /api/hls/start is needed — but it's made BEFORE the
     * current player is touched: that one just pauses on its frame (with
     * Artplayer's spinner) while the new stream warms up, and is swapped out
     * only once the start succeeded. If it fails (500 from a broken plan,
     * 4xx, network), the user gets a toast with the reason and keeps the
     * previous stream — resumed in place when still servable, else re-opened
     * on the previous plan at the same position — and the menus still show
     * the previous selection, since entry state only changes on success.
     * (It used to tear the player down first and start blind; a failed start
     * then left a black overlay with no HUD that survived SPA navigation.)
     *
     * Each call takes a start token (beginOp): a newer switch aborts this
     * one's start, and Back / Next / close (detach) make it stale, so a late
     * answer stops its own session instead of resurrecting a player.
     *
     * @param {'audio'|'quality'|'sync'|'burn'} kind  Picks the failure message.
     * @returns {Promise<boolean>} true once the new plan plays (false: kept
     *                              the previous one, superseded, or stale).
     */
    async function restartStream(elementId, changes, kind) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.adapter || !entry.dotnetRef || !entry.mediaPath) return false;
        const op = beginOp(elementId);
        // A switch still warming up is superseded: abort its start.
        if (entry.pendingSwitch) { try { entry.pendingSwitch.abort(); } catch {} }
        const ctl = new AbortController();
        entry.pendingSwitch = ctl;
        // State as of the user's first click — a superseded switch already
        // froze the player, so reuse its snapshot rather than read "paused".
        const snap = entry.switchSnap || captureState(entry);
        entry.switchSnap = snap;
        const prev = currentPlan(entry);
        const plan = Object.assign({}, prev, changes);

        entry.adapter.pause();
        if (entry.art) { try { entry.art.loading.show = true; } catch {} }

        let started = null, failure = null;
        try {
            started = await startStream(entry.mediaPath,
                Object.assign({
                    resumeSec: snap.pos, offsets: audioOffsetsFor(plan.audioOffsetMs), caps: CAPS,
                    tier: (KEEP.get(elementId) || {}).tierFloor || 0,
                    fallbackMbps: (KEEP.get(elementId) || {}).fallbackMbps,
                }, plan), ctl.signal);
        } catch (err) { failure = err; }
        if (entry.pendingSwitch === ctl) entry.pendingSwitch = null;
        if (!isLiveOp(elementId, op)) {
            // Closed, next episode, or a newer switch took over meanwhile.
            if (started) stopSession(started.sessionToken);
            return false;
        }
        entry.switchSnap = null;

        if (started) {
            await attachStream(elementId, entry.dotnetRef, entry.mediaPath,
                Object.assign({}, plan, { forceResumeSec: snap.pos, prestarted: started, restore: snap }), op);
            return true;
        }

        if (failure && failure.superseded) {
            // Another start for this file from this device (another tab)
            // became playable first and owns playback now; it also retires
            // our previous session, so neither resume nor re-open it — that
            // would just take the stream back. Leave the player paused.
            console.info('animarrPlayer: ' + kind + ' switch superseded by a newer start, dropping it');
            if (entry.art) { try { entry.art.loading.show = false; } catch {} }
            return false;
        }
        console.warn('animarrPlayer: ' + kind + ' switch failed, keeping the previous stream', failure);
        const title  = t(kind === 'audio' ? 'error_audio' : kind === 'sync' ? 'error_sync'
                       : kind === 'burn' ? 'error_burn' : 'error_quality');
        const detail = (failure && failure.reason) || '';
        // The server keeps the previous session alive until a replacement
        // actually plays, so this is normally true and the old stream simply
        // continues; re-opening the previous plan is the fallback for a
        // session that died meanwhile (reaped, server restarted).
        const alive  = await previousStreamAlive(entry);
        if (!isLiveOp(elementId, op) || WIRED.get(elementId) !== entry) return false;
        if (alive) {
            if (entry.art) { try { entry.art.loading.show = false; } catch {} }
            if (!snap.paused) entry.adapter.play();
            showToast(entry.hudRoot, title, detail);
            return false;
        }
        await attachStream(elementId, entry.dotnetRef, entry.mediaPath,
            Object.assign({}, prev, { forceResumeSec: snap.pos, toast: { title, detail }, restore: snap }), op);
        return false;
    }

    /** Restart with embedded subtitle track s:N burned into the picture
     *  (nothing on top of it), remembered as that subtitle choice. */
    async function burnSubtitleIn(elementId, index) {
        const entry = WIRED.get(elementId);
        if (!entry || entry.currentBurnSub === index) return false;
        const prev = { idx: entry.currentSubIdx, ext: entry.currentExtSubPath };
        disposeSubRenderer(entry);
        if (entry.adapter) entry.adapter.setSubtitle(null);
        entry.currentSubIdx = null; entry.currentExtSubPath = null;
        const ok = await restartStream(elementId, { burnSubtitle: index }, 'burn');
        if (ok) queueTrackPrefs(elementId, { subtitle: { kind: 'embedded', index } });
        else if (WIRED.get(elementId) === entry) {
            applySubtitleChoice(entry, prev.idx != null ? { idx: prev.idx } : prev.ext ? { ext: prev.ext } : null);
        }
        return ok;
    }
    /** Leave a burned-in subtitle for `choice` ({ idx } | { ext } | null):
     *  a restart without the burn, the new choice drawn on top as usual. */
    async function unburnSubtitle(elementId, choice) {
        const entry = WIRED.get(elementId);
        if (!entry) return false;
        // captureState carries these into the restarted stream (restore.sub).
        entry.currentSubIdx = choice && choice.idx != null ? choice.idx : null;
        entry.currentExtSubPath = choice && choice.ext ? choice.ext : null;
        return restartStream(elementId, { burnSubtitle: null }, 'burn');
    }

    /**
     * Switch to a different audio track in the source (`-map 0:a:N`), or to
     * an external dub file (`-map 1:a:0`). Restarts the stream at the current
     * position, keeping the quality caps — see restartStream.
     */
    async function switchAudio(elementId, sel) {
        const entry = WIRED.get(elementId);
        const ext = (sel && sel.externalAudioPath) || null;
        return restartStream(elementId, {
            audioTrackIndex:   (sel && Number.isFinite(sel.audioTrackIndex)) ? Math.max(0, sel.audioTrackIndex) : 0,
            externalAudioPath: ext,
            // That audio's own sync: a dub's DELAY, else none (no memory here —
            // the user's picks go through chooseAudio).
            audioOffsetMs:     ext && entry ? dubDelayMs(entry, ext) : 0,
        }, 'audio');
    }

    // Back-compat thin wrapper: switch to an in-file audio stream by index
    // (clears any active external dub).
    async function switchAudioTrack(elementId, audioTrackIndex) {
        return switchAudio(elementId, { audioTrackIndex, externalAudioPath: null });
    }

    /**
     * Switch output quality: max height cap (server downscales / re-encodes
     * below the source height) and/or bitrate cap, 0 = original. Restarts the
     * stream at the current position, keeping the audio track and any
     * external dub — see restartStream.
     */
    async function switchQuality(elementId, maxHeight, maxBitrate) {
        return restartStream(elementId, {
            maxHeight:  Math.max(0, maxHeight || 0),
            maxBitrate: Math.max(0, maxBitrate || 0),
        }, 'quality');
    }

    /** Session metadata from Blazor (title, skip segments, up-next, trickplay,
     *  lock screen, prev/next). Kept per element so every restart of the
     *  stream re-applies it (attach → applyMediaSession). */
    function setMediaSession(elementId, meta, handlers) {
        // Only for a player that's actually open (attach may still be starting
        // or have failed — it's applied once a stream is up).
        if (!WIRED.has(elementId)) return;
        keepFor(elementId).session = { meta: meta || null, handlers: handlers || null };
        applyMediaSession(elementId);
    }

    function applyMediaSession(elementId) {
        const entry = WIRED.get(elementId);
        const kept  = KEEP.get(elementId);
        if (!entry || !entry.adapter || !kept || !kept.session) return;
        const { meta, handlers } = kept.session;
        if (entry.hud) {
            const kicker = meta?.hudKicker || '';
            const name   = meta?.hudName   || meta?.title || '';
            entry.hud.setTitle(kicker, name);
        }
        // Up-next overlay data (end-of-episode autoplay card). Read live by the
        // HUD's updateUpNext on each timeupdate; copied onto each new entry
        // from KEEP, so it lasts until the player closes / the next episode.
        entry.nextAvailable = !!(meta && meta.nextAvailable);
        if (entry.hud && entry.hud.setNav) {
            entry.hud.setNav(!!(meta && meta.prevAvailable), !!(meta && meta.nextAvailable));
        }
        entry.upNext = {
            eyebrow: (meta && meta.upNextEyebrow) || t('up_next'),
            name:    (meta && meta.upNextName)    || '',
            play:    (meta && meta.upNextPlay)    || t('up_next_play'),
            dismiss: (meta && meta.upNextDismiss) || t('up_next_dismiss'),
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
        entry.skipIntroLabel   = (meta && meta.skipIntroLabel)   || entry.skipIntroLabel   || t('skip_intro');
        entry.skipCreditsLabel = (meta && meta.skipCreditsLabel) || entry.skipCreditsLabel || t('skip_credits');
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
        if (!WIRED.has(elementId)) return;
        if (!meta || !meta.mediaId || !meta.title) return;
        // In KEEP (read by sendProgress) so a stream restart doesn't drop it.
        keepFor(elementId).watchNext = {
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
        setStyle, switchAudioTrack, switchAudio, setPrefs, preload,
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
