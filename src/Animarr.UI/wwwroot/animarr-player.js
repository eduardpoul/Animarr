// Artplayer-based player bridge.
//
// What attach() does, in order:
//   1. Ask .NET for the resume position (so we can seek straight there).
//   2. Calibrate the audio offset (HW channel only — SW is manual-via-settings).
//   3. POST /api/hls/start — server decides Direct Play vs HLS plan and
//      returns either a directPlayUrl OR (token, manifestUrl).
//   4. Probe /api/probe — extract subtitle tracks + push media-info to .NET
//      for the chip strip.
//   5. Instantiate Artplayer in the container div with:
//        • our HLS source (via customType handler that drives hls.js)
//        • the subtitle list as Artplayer's subtitle config
//        • custom controls (Cast, mpv, Close) in the bottom chrome —
//          alongside Artplayer's built-in play/scrub/settings/fullscreen
//   6. Wire video:timeupdate → .NET RecordProgressAsync (every ~5s).
//   7. Keepalive ping every 30s (HLS only — Direct Play has no server session).
//
// detach() destroys the Artplayer instance, kills the hls.js attached to it,
// stops keepalive, and DELETEs the HLS session.

(function () {
    // elementId → { art, abort, refRef, sessionToken, keepaliveTimer, totalDuration, resumeOffset }
    const WIRED = new Map();

    // Hosts that don't share an origin with the API (MAUI's BlazorWebView is
    // the only one currently — its WebView serves index.html from a local
    // virtual host while the API lives on a remote tower) prepend a base URL
    // to every /api/ path. WASM / Blazor-Server hosts leave it as '' so the
    // page-origin behaviour stays unchanged.
    const apiBase = () => (typeof window !== 'undefined' && window.animarrApiBase) || '';
    const apiUrl  = (path) => apiBase() + path;

    // SVG icons used by custom controls. Stroked-line style to match the
    // rest of the app (DIcon set in C#).
    const ICONS = {
        cast:     '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M2 17a3 3 0 0 1 3 3"/><path d="M2 13a7 7 0 0 1 7 7"/><path d="M2 9a11 11 0 0 1 11 11"/><path d="M2 5h17a2 2 0 0 1 2 2v9"/></svg>',
        external: '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/></svg>',
        close:    '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M6 6l12 12M18 6l-12 12"/></svg>',
        // "Sliders horizontal" — three knobs on tracks. Universally readable
        // as "tune / adjust". Previous headphones icon had an r=0 circle
        // which doesn't render and a confusing path that read as nothing.
        audioSync: '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><line x1="21" y1="6" x2="14" y2="6"/><line x1="10" y1="6" x2="3" y2="6"/><line x1="21" y1="12" x2="12" y2="12"/><line x1="8" y1="12" x2="3" y2="12"/><line x1="21" y1="18" x2="16" y2="18"/><line x1="12" y1="18" x2="3" y2="18"/><circle cx="12" cy="6" r="2" fill="currentColor"/><circle cx="10" cy="12" r="2" fill="currentColor"/><circle cx="14" cy="18" r="2" fill="currentColor"/></svg>',
    };

    /** Build the media-info chip strip HTML from a probe-derived info object.
     *  Rendered inside Artplayer as a layer (see attach), so the chips live
     *  in the player DOM tree and survive fullscreen + z-index battles with
     *  Artplayer chrome. Previously rendered by Razor as a sibling of the
     *  Artplayer container, which had visibility issues we couldn't pin down. */
    function buildChipStripHtml(info) {
        if (!info) return '';
        const chipStyle = 'display:inline-flex;align-items:center;height:22px;padding:0 8px;border-radius:4px;background:rgba(0,0,0,0.65);color:rgba(255,255,255,0.92);font-size:11px;font-weight:500;letter-spacing:0.02em;white-space:nowrap;backdrop-filter:blur(4px);';
        const hdrStyle = chipStyle + 'background:linear-gradient(180deg,#c89c4a,#8e6a26);color:#fff;';
        const tierStyle = chipStyle + 'background:rgba(120,200,255,0.28);color:#9bd6ff;';
        const directplayStyle = chipStyle + 'background:rgba(120,255,180,0.28);color:#8be5b2;';
        const chips = [];
        if (info.width > 0 && info.height > 0) {
            chips.push(`<span style="${chipStyle}">${info.width}×${info.height}</span>`);
        }
        if (info.videoCodec) {
            chips.push(`<span style="${chipStyle}">${info.videoCodec.toUpperCase()}</span>`);
        }
        if (info.bitDepth >= 10) {
            chips.push(`<span style="${chipStyle}">10-bit</span>`);
        }
        (info.hdrFormats || []).forEach(fmt => {
            const label = fmt === 'dolbyvision' ? 'Dolby Vision'
                       : fmt === 'hdr10' ? 'HDR10'
                       : fmt === 'hlg' ? 'HLG'
                       : fmt.toUpperCase();
            chips.push(`<span style="${hdrStyle}">${label}</span>`);
        });
        if (info.audioCodec) {
            const ch = info.audioChannels || 0;
            const chLabel = ch === 1 ? 'Mono' : ch === 2 ? 'Stereo'
                          : ch === 6 ? '5.1' : ch === 7 ? '6.1' : ch === 8 ? '7.1'
                          : ch > 0 ? `${ch}ch` : '';
            chips.push(`<span style="${chipStyle}">${info.audioCodec.toUpperCase()}${chLabel ? ' ' + chLabel : ''}</span>`);
        }
        const tier = info.playbackTier === 'directplay' ? 'Direct Play' : 'HLS';
        const tierFlavor = info.playbackTier === 'directplay' ? directplayStyle : tierStyle;
        chips.push(`<span style="${tierFlavor}">${tier}</span>`);
        return chips.join('');
    }

    /** Picks which audio-offset channel the slider should tune for the
     *  current playback. Returns 'hw' / 'sw' / null. */
    function determineOffsetChannel(info) {
        if (!info) return null;
        if (info.playbackTier === 'directplay') return null;  // no transcode → no offset path
        const codec = (info.videoCodec || '').toLowerCase();
        if (codec === 'h264') return null;  // TS MPEG path uses PCR, no -itsoffset
        if (codec === 'hevc') {
            // HEVC 10-bit / HDR / DV goes through fMP4 stream-copy (SW slider)
            // HEVC 8-bit on a VAAPI-capable host goes through h264 re-encode (HW slider)
            return info.bitDepth >= 10 ? 'sw' : 'hw';
        }
        return 'hw';  // unknown codec — assume HW path
    }

    /** Show / hide a small slider popup anchored to the Artplayer chrome.
     *  All styles inlined because Blazor scoped CSS (the .razor.css system)
     *  appends a [b-xxxxxx] attribute selector to every rule, which won't
     *  match JS-injected DOM — without inline styles the popup renders
     *  unstyled (transparent, zero-sized) and looks like nothing happened. */
    function toggleAudioOffsetPopup(art, channel) {
        console.info('animarr offset popup: toggle', { channel, artHas: !!art });
        if (!window.animarrCalibrate) {
            console.warn('animarr offset popup: animarrCalibrate missing');
            return;
        }
        // Artplayer 5 exposes the container at `art.template.$player` (the
        // inner wrapper that goes fullscreen) and `art.container` (the original
        // mount div). Use the player wrapper if available so the popup survives
        // browser fullscreen.
        const root = art?.template?.$player || art?.container;
        if (!root) {
            console.warn('animarr offset popup: no Artplayer root found');
            return;
        }
        let popup = root.querySelector('.vp-offset-popup');
        if (popup) { popup.remove(); return; }

        const current = window.animarrCalibrate.getCached(channel)
            ?? (channel === 'hw' ? 70 : 0);
        const min = -200;
        const max = channel === 'sw' ? 700 : 300;
        const label = channel === 'sw'
            ? 'HDR / 10-bit (stream-copy)'
            : 'Standard (re-encode)';

        popup = document.createElement('div');
        popup.className = 'vp-offset-popup';
        popup.style.cssText = [
            'position:absolute',
            'right:12px',
            'bottom:72px',
            'z-index:9999',
            'width:280px',
            'padding:12px 14px',
            'background:rgba(24,24,24,0.98)',
            'border:1px solid rgba(255,255,255,0.12)',
            'border-radius:8px',
            'box-shadow:0 10px 32px rgba(0,0,0,0.5)',
            'color:rgba(255,255,255,0.92)',
            'font-size:12px',
            'font-family:inherit',
            'pointer-events:auto',
        ].join(';');
        popup.innerHTML = `
            <div style="font-weight:500;margin-bottom:8px;color:rgba(255,255,255,0.95);">
                Audio sync · ${label}
            </div>
            <div style="display:flex;align-items:center;gap:10px;margin-bottom:6px;">
                <input type="range" min="${min}" max="${max}" step="5" value="${current}"
                       style="flex:1;accent-color:#ff6b00;">
                <span class="vp-offset-popup__value"
                      style="min-width:60px;text-align:right;font-variant-numeric:tabular-nums;color:rgba(255,255,255,0.7);">
                    ${current} ms
                </span>
            </div>
            <div style="font-size:11px;color:rgba(255,255,255,0.55);margin-top:6px;">
                Reopen the video to apply (saves immediately)
            </div>
        `;
        // Stop chrome-hide events while user interacts with the popup.
        popup.addEventListener('mousedown', e => e.stopPropagation());
        popup.addEventListener('click', e => e.stopPropagation());
        root.appendChild(popup);
        console.info('animarr offset popup: rendered into', root);

        const range = popup.querySelector('input[type=range]');
        const valueEl = popup.querySelector('.vp-offset-popup__value');
        range.addEventListener('input', () => {
            const v = parseInt(range.value, 10);
            valueEl.textContent = v + ' ms';
            window.animarrCalibrate.setManual(channel, v);
        });

        // Click outside → close.
        const onDocClick = (e) => {
            if (!popup.contains(e.target)) {
                popup.remove();
                document.removeEventListener('click', onDocClick, true);
            }
        };
        // Defer to avoid catching the click that opened us.
        setTimeout(() => document.addEventListener('click', onDocClick, true), 0);
    }

    /**
     * @param {string} elementId  DOM id of the container div Artplayer mounts into
     * @param {DotNetObjectReference} dotnetRef
     * @param {string} mediaPath  Absolute file path (server-side)
     */
    async function attach(elementId, dotnetRef, mediaPath) {
        const el = document.getElementById(elementId);
        if (!el) {
            console.warn('animarrPlayer.attach: container not found', elementId);
            return;
        }
        if (WIRED.has(elementId)) {
            console.warn('animarrPlayer: re-attaching without detach, cleaning up first');
            detach(elementId);
        }
        if (typeof window.Artplayer !== 'function') {
            console.error('animarrPlayer: Artplayer not loaded from CDN');
            return;
        }

        const abort = new AbortController();
        const refRef = { current: dotnetRef };
        const entry = { abort, refRef, art: null, sessionToken: null, keepaliveTimer: null,
                        totalDuration: 0, resumeOffset: 0 };
        WIRED.set(elementId, entry);

        // ── 1) Resume position from .NET ───────────────────────────────────
        let resumeSec = 0;
        try {
            const sec = await dotnetRef.invokeMethodAsync('GetResumePositionSec');
            if (typeof sec === 'number' && sec > 5) resumeSec = sec;
        } catch (e) { /* no resume info, start at 0 */ }
        if (abort.signal.aborted) return;

        // ── 2) Audio-sync calibration (HW channel only) + SW from settings ─
        let audioOffsetMsHw = 70;  // matches DEFAULT_OFFSET_MS
        let audioOffsetMsSw = 0;
        if (window.animarrCalibrate) {
            try { audioOffsetMsHw = await window.animarrCalibrate.calibrate(); }
            catch (e) { console.warn('animarr: calibrate threw', e); }
            const sw = window.animarrCalibrate.getCached('sw');
            if (sw !== null) audioOffsetMsSw = sw;
        }
        if (abort.signal.aborted) return;

        // ── 3) Start session (Direct Play OR HLS, server decides) ──────────
        let manifestUrl = null;
        let directPlayUrl = null;
        const startUrl = apiUrl('/api/hls/start?path=' + encodeURIComponent(mediaPath)
            + (resumeSec > 0 ? '&seek=' + resumeSec.toFixed(2) : '')
            + '&audioOffsetHwMs=' + audioOffsetMsHw
            + '&audioOffsetSwMs=' + audioOffsetMsSw);
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

        // ── 4) Probe for subtitles + push media-info to .NET ───────────────
        let subtitleList = [];
        let mediaInfo = null;  // captured so we can decide offset-channel below
        if (mediaPath) {
            try {
                const probeRes = await fetch(apiUrl('/api/probe?path=' + encodeURIComponent(mediaPath)),
                    { signal: abort.signal });
                if (abort.signal.aborted) return;
                if (probeRes.ok) {
                    const data = await probeRes.json();
                    const subs = (data.streams || []).filter(s => s.codec_type === 'subtitle');
                    subtitleList = subs.map((s, idx) => {
                        const lang  = (s.tags && (s.tags.language || s.tags.LANGUAGE)) || 'und';
                        const label = (s.tags && (s.tags.title    || s.tags.TITLE))    || `Track ${idx + 1}`;
                        return {
                            name: label + (lang !== 'und' ? ` (${lang})` : ''),
                            url:  apiUrl('/api/subtitle?path=' + encodeURIComponent(mediaPath))
                                + '&track=' + idx + '&format=webvtt',
                            default: !!(s.disposition && s.disposition.default)
                                  || (subs.length === 1 && idx === 0),
                        };
                    });

                    // Media-info push to .NET — same as before, drives the chip strip.
                    const v = (data.streams || []).find(s => s.codec_type === 'video');
                    const a = (data.streams || []).find(s => s.codec_type === 'audio');
                    console.info('animarr probe streams:',
                        { hasVideo: !!v, hasAudio: !!a, totalStreams: (data.streams || []).length });
                    if (v) {
                        const pix = (v.pix_fmt || '').toLowerCase();
                        const is10bit = pix.includes('10le') || pix.includes('10be')
                                     || (v.profile || '').toLowerCase().includes('main 10');
                        const hasDv = (v.side_data_list || []).some(sd =>
                            (sd.side_data_type || '').toUpperCase().includes('DOVI'));
                        // Collect ALL detected HDR formats — a UHD BluRay DV
                        // title typically carries an HDR10 base layer under
                        // the DV metadata, so both chips are accurate. Previous
                        // else-if chain skipped HDR10 detection once DV was
                        // found, which made the chip strip look incomplete
                        // (just "DV", no "HDR10" — user noticed).
                        const xfer = (v.color_transfer || '').toLowerCase();
                        const hdrFormats = [];
                        if (hasDv) hdrFormats.push('dolbyvision');
                        if (xfer === 'smpte2084') hdrFormats.push('hdr10');
                        else if (xfer === 'arib-std-b67') hdrFormats.push('hlg');
                        mediaInfo = {
                            videoCodec: v.codec_name || '',
                            width: v.width || 0,
                            height: v.height || 0,
                            bitDepth: is10bit ? 10 : 8,
                            // Legacy single field for back-compat with any older
                            // C#-side code that still reads `Hdr`.
                            hdr: hdrFormats[0] || 'sdr',
                            // The new authoritative list — Razor renders one
                            // chip per entry.
                            hdrFormats,
                            audioCodec: a ? (a.codec_name || '') : '',
                            audioChannels: a ? (a.channels || 0) : 0,
                            playbackTier: directPlayUrl ? 'directplay' : 'hls',
                        };
                        dotnetRef.invokeMethodAsync('OnPlayerMediaInfo', mediaInfo)
                            .catch(e => console.warn('animarr: OnPlayerMediaInfo failed', e));
                    }
                }
            } catch (err) {
                if (err.name !== 'AbortError') console.warn('probe failed', err);
            }
        }

        // Get mpv:// link from .NET so the mpv button has the right href.
        let mpvHref = '#';
        try { mpvHref = await dotnetRef.invokeMethodAsync('GetMpvDeepLink'); } catch {}

        // Decide whether to show the audio-offset button — only meaningful
        // when there's actually a transcode path that applies -itsoffset.
        const offsetChannel = determineOffsetChannel(mediaInfo);

        // ── 5) Instantiate Artplayer ───────────────────────────────────────
        // For cross-origin hosts (MAUI BlazorWebView pointing at a remote
        // server) we re-anchor the server-supplied URLs against the API
        // base — server returns them as page-relative paths and the
        // WebView's local origin would resolve them to nowhere.
        const playUrl = directPlayUrl
            ? (directPlayUrl.startsWith('/') ? apiUrl(directPlayUrl) : directPlayUrl)
            : (manifestUrl.startsWith('/')   ? apiUrl(manifestUrl)   : manifestUrl);
        const isHls   = !directPlayUrl;
        const fileExt = mediaPath.toLowerCase().split('.').pop();

        const art = new window.Artplayer({
            container: el,
            url: playUrl,
            type: isHls ? 'm3u8' : (fileExt || 'mp4'),
            // hls.js wiring as Artplayer custom type handler. This is where
            // we apply all the buffer/timeout tuning that was previously
            // injected via Vidstack provider-events.
            customType: isHls ? {
                m3u8: (video, url) => {
                    if (!window.Hls || !window.Hls.isSupported()) {
                        // Safari has native HLS — just set src.
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
                    console.info('animarr: Artplayer + hls.js wired (frag=60s, holeTol=4s)');

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
            volume: 1,
            // ── Built-in features that replace our old custom buttons ──
            // aspectRatio: false — Artplayer's built-in only ships
            // Default/4:3/16:9. We provide our own selector below with 21:9
            // for ultrawide monitors and 2.35:1 / 2.39:1 for Cinemascope.
            aspectRatio: false,
            screenshot: true,     // screenshot button (bonus)
            setting: true,        // unified settings menu
            playbackRate: true,   // speed picker
            fullscreen: true,
            fullscreenWeb: false, // we want native fullscreen, not CSS-only
            pip: true,
            airplay: false,       // we use DLNA via our own Cast button
            miniProgressBar: true, // tiny progress bar visible when chrome hidden
            mutex: true,
            backdrop: true,
            hotkey: true,
            theme: '#ff6b00',
            lang: 'en',
            moreVideoAttr: { crossorigin: 'anonymous' },
            // Subtitles — Artplayer accepts ONE active subtitle at a time;
            // multiple options go through `selector` in settings (see below).
            subtitle: subtitleList.length > 0 ? {
                url: (subtitleList.find(s => s.default) || subtitleList[0]).url,
                type: 'webvtt',
                encoding: 'utf-8',
                escape: false,
            } : undefined,
            // Media-info chip strip — rendered INSIDE Artplayer as a layer
            // (not as a sibling overlay), so it sits cleanly in the player
            // DOM, survives browser fullscreen, and isn't fighting Artplayer's
            // own chrome for z-index.
            layers: mediaInfo ? [{
                name: 'media-info',
                html: buildChipStripHtml(mediaInfo),
                style: {
                    position: 'absolute',
                    top: '10px',
                    left: '12px',
                    display: 'flex',
                    alignItems: 'center',
                    gap: '6px',
                    flexWrap: 'wrap',
                    pointerEvents: 'none',
                    zIndex: '20',
                    textShadow: '0 1px 2px rgba(0,0,0,0.6)',
                },
            }] : [],
            // ── Custom buttons in the bottom chrome ──
            // position:'right' puts them in the right cluster next to
            // Artplayer's built-in fullscreen/settings buttons. They live
            // in the SAME control bar — no more separate top header.
            controls: [
                // Audio-offset slider — only shown when current playback has
                // an -itsoffset transcoding path that actually uses the value
                // (i.e. HEVC via HLS). H.264/MPEG-TS uses native PCR sync,
                // Direct Play doesn't transcode at all → no slider in those
                // cases. The popup writes the new value to localStorage
                // immediately; it takes effect on next reopen of the file.
                ...(offsetChannel ? [{
                    name: 'audio-offset',
                    position: 'right',
                    html: ICONS.audioSync,
                    tooltip: `Audio sync (${offsetChannel === 'sw' ? 'HDR / 10-bit' : 'standard'})`,
                    click() { toggleAudioOffsetPopup(art, offsetChannel); },
                }] : []),
                {
                    name: 'cast',
                    position: 'right',
                    html: ICONS.cast,
                    tooltip: 'Cast to TV (DLNA)',
                    click() {
                        refRef.current?.invokeMethodAsync('ToggleCastMenuFromJs').catch(() => {});
                    },
                },
                {
                    name: 'mpv',
                    position: 'right',
                    html: `<a href="${mpvHref.replace(/"/g, '&quot;')}" title="Open in mpv" style="display:inline-flex;align-items:center;color:inherit;text-decoration:none;">${ICONS.external}</a>`,
                    tooltip: 'Open in mpv (native HDR/DV/Atmos)',
                    // No click — let the <a> handle navigation natively.
                },
                {
                    name: 'close',
                    position: 'right',
                    html: ICONS.close,
                    tooltip: 'Close',
                    click() {
                        refRef.current?.invokeMethodAsync('ClosePlayerFromJs').catch(() => {});
                    },
                },
            ],
            // Settings menu entries — always include our custom aspect-ratio
            // picker (Artplayer's built-in only had Default/4:3/16:9, no 21:9
            // for ultrawide displays). Subtitle picker added when source has
            // multiple subtitle tracks.
            settings: (() => {
                const items = [];
                items.push({
                    width: 220,
                    html: 'Aspect Ratio',
                    tooltip: 'Default',
                    selector: [
                        { html: 'Default',          value: 'default', default: true },
                        { html: '21:9 (ultrawide)', value: '21:9' },
                        { html: '16:9',             value: '16:9' },
                        { html: '4:3',              value: '4:3' },
                    ],
                    onSelect(item) {
                        try {
                            // Forcing an aspect ratio just resizes the video
                            // element's BOX. Without object-fit:cover the
                            // source content keeps its natural ratio and is
                            // letterboxed inside the new box, defeating the
                            // purpose of forcing an aspect. Picking 21:9 on a
                            // 16:9 source-with-baked-cinemascope-bars works
                            // ONLY when we also crop via object-fit:cover —
                            // the baked black bars get cropped off the top
                            // and bottom first because cropping happens at
                            // the edges.
                            art.aspectRatio = item.value;
                            const video = art.video || art.template?.$video;
                            if (video) {
                                if (item.value === 'default') {
                                    video.style.objectFit = '';
                                    video.style.aspectRatio = '';
                                } else {
                                    video.style.objectFit = 'cover';
                                    // Defensive: also set CSS aspect-ratio in
                                    // case Artplayer didn't apply it for some
                                    // reason (versions differ on the setter).
                                    video.style.aspectRatio = item.value.replace(':', ' / ');
                                }
                            }
                            console.info('animarr: aspectRatio →', item.value, 'objectFit →',
                                item.value === 'default' ? 'contain' : 'cover');
                        } catch (e) { console.warn('aspectRatio set failed', e); }
                        return item.html;
                    },
                });
                if (subtitleList.length > 1) {
                    items.push({
                        width: 240,
                        html: 'Subtitle',
                        tooltip: subtitleList.find(s => s.default)?.name || subtitleList[0].name,
                        icon: '<i style="font-style:normal;">CC</i>',
                        selector: subtitleList.map(s => ({
                            html: s.name,
                            url: s.url,
                            default: !!s.default,
                        })),
                        onSelect(item) {
                            try { art.subtitle.switch(item.url, { name: item.html, escape: false }); }
                            catch (e) { console.warn('subtitle switch failed', e); }
                            return item.html;
                        },
                    });
                }
                return items;
            })(),
        });

        entry.art = art;

        // ── 5b) MediaSession + Picture-in-Picture wiring ───────────────────
        // Exposes play/pause/seek to:
        //   • iOS Now Playing centre (Control Centre + lock screen)
        //   • Android system media notification (collapsed + expanded)
        //   • macOS Now Playing (Touch Bar + lock-screen widget)
        //   • Chromium Global Media Controls bubble
        //   • Bluetooth-remote / car-stereo media buttons
        //
        // Metadata (title/poster) gets pushed in via setMediaSession() once
        // the .NET side knows what's playing — see below. Action handlers
        // are wired here so they take effect even before metadata arrives.
        try {
            if ('mediaSession' in navigator) {
                const ms = navigator.mediaSession;
                ms.setActionHandler('play',     () => { try { art.play();  } catch {} });
                ms.setActionHandler('pause',    () => { try { art.pause(); } catch {} });
                ms.setActionHandler('seekto', d => {
                    if (typeof d.seekTime === 'number') {
                        try { art.currentTime = d.seekTime; } catch {}
                    }
                });
                ms.setActionHandler('seekbackward', d => {
                    const step = (d && d.seekOffset) || 10;
                    try { art.currentTime = Math.max(0, (art.currentTime || 0) - step); } catch {}
                });
                ms.setActionHandler('seekforward', d => {
                    const step = (d && d.seekOffset) || 10;
                    try { art.currentTime = (art.currentTime || 0) + step; } catch {}
                });
                ms.setActionHandler('stop', () => { try { art.pause(); } catch {} });
                // Position state — lets the lock-screen scrubber show real progress.
                art.on('video:timeupdate', () => {
                    if (!('setPositionState' in ms)) return;
                    const dur = entry.totalDuration || art.duration || 0;
                    if (!Number.isFinite(dur) || dur <= 0) return;
                    try {
                        ms.setPositionState({
                            duration:     dur,
                            playbackRate: art.playbackRate || 1,
                            position:     Math.min(art.currentTime || 0, dur),
                        });
                    } catch { /* setPositionState throws on bad numbers — bail */ }
                });
                art.on('video:play',  () => { ms.playbackState = 'playing'; });
                art.on('video:pause', () => { ms.playbackState = 'paused';  });
            }
        } catch (e) { console.warn('mediaSession wire-up failed', e); }

        // Picture-in-Picture — attempt automatic enter on visibility change
        // (user switched tabs / received a call) so playback floats over the
        // OS. The browser may decline if the document is fully hidden or PiP
        // isn't allowed; we swallow that.
        try {
            const video = art.video;
            if (video && document.pictureInPictureEnabled && !video.disablePictureInPicture) {
                const onVisibility = () => {
                    if (document.visibilityState === 'hidden' &&
                        !video.paused &&
                        document.pictureInPictureElement !== video) {
                        video.requestPictureInPicture().catch(() => { /* user denied */ });
                    }
                };
                document.addEventListener('visibilitychange', onVisibility);
                entry._pipVisibilityHandler = onVisibility;
            }
        } catch { /* PiP unsupported — fine */ }

        // Chip strip stays visible throughout playback — previous attempt to
        // sync with Artplayer's chrome auto-hide via mouse-activity timer
        // either didn't get mouse events (Artplayer eats them on inner DOM)
        // or fired with wrong cadence. The chips are small and tucked in the
        // top-left corner; keeping them visible is the predictable behaviour
        // and what the user explicitly wants (they're there to surface what's
        // playing).

        // ── 6) Resume seek + progress reporting ─────────────────────────────
        if (resumeSec > 0) {
            const seekOnce = () => {
                try { art.currentTime = resumeSec; }
                catch (e) { console.warn('resume seek failed', e); }
            };
            // Wait for video metadata before seeking, otherwise Artplayer
            // ignores currentTime sets that arrive before the video element
            // knows its duration.
            art.once('video:loadedmetadata', seekOnce);
        }

        let lastTick = 0;
        let lastPosForDelta = resumeSec;
        const sendProgress = () => {
            const r = refRef.current;
            if (!r) return;
            const pos = art.currentTime || 0;
            const dur = entry.totalDuration || art.duration || 0;
            let delta = 0;
            if (art.playing && pos > lastPosForDelta && (pos - lastPosForDelta) < 30) {
                delta = Math.round(pos - lastPosForDelta);
            }
            lastPosForDelta = pos;
            r.invokeMethodAsync('OnPlayerProgress', pos, dur, delta).catch(() => {});
        };
        art.on('video:timeupdate', () => {
            const now = Date.now();
            if (now - lastTick < 5000) return;
            lastTick = now;
            sendProgress();
        });
        art.on('video:pause', sendProgress);
        art.on('video:ended', sendProgress);

        // ── 7) Keepalive for HLS sessions (Direct Play has no token) ───────
        if (entry.sessionToken) {
            entry.keepaliveTimer = setInterval(() => {
                if (!entry.sessionToken) return;
                fetch(apiUrl('/api/hls/keepalive?token=' + encodeURIComponent(entry.sessionToken)),
                    { method: 'POST', signal: abort.signal })
                    .catch(() => { /* tick will retry */ });
            }, 30000);
        }
    }

    function flush(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.art) return;
        // Fire a pause-equivalent so .NET gets a final progress write before
        // teardown. video:pause listener does the actual call.
        try { entry.art.pause(); } catch {}
    }

    function detach(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry) return;
        // 1) Drop .NET ref so late callbacks bail.
        entry.refRef.current = null;
        // 2) Stop keepalive + abort in-flight fetches.
        if (entry.keepaliveTimer) { clearInterval(entry.keepaliveTimer); entry.keepaliveTimer = null; }
        entry.abort.abort();
        // 3) Tell server to free ffmpeg + tmp dir.
        if (entry.sessionToken) {
            try {
                fetch(apiUrl('/api/hls/' + encodeURIComponent(entry.sessionToken)),
                    { method: 'DELETE', keepalive: true });
            } catch { }
        }
        // 4) Tear down PiP visibility listener + media session metadata.
        if (entry._pipVisibilityHandler) {
            document.removeEventListener('visibilitychange', entry._pipVisibilityHandler);
            entry._pipVisibilityHandler = null;
        }
        try {
            if ('mediaSession' in navigator) {
                navigator.mediaSession.metadata = null;
                navigator.mediaSession.playbackState = 'none';
                // Clear handlers so a future detach->reattach doesn't double-fire.
                ['play','pause','seekto','seekbackward','seekforward','stop',
                 'previoustrack','nexttrack'].forEach(a => {
                    try { navigator.mediaSession.setActionHandler(a, null); } catch {}
                });
            }
        } catch {}
        // 5) Destroy Artplayer (which destroys its hls.js too via on('destroy')).
        if (entry.art) {
            try { entry.art.destroy(true); } catch {}
            entry.art = null;
        }
        WIRED.delete(elementId);
    }

    /**
     * Push metadata to the OS-level Now Playing surfaces (lock screen,
     * Control Centre, system media notification). Called from MediaDetail
     * after the title is known. Safe to call multiple times — replaces
     * existing metadata.
     *
     * @param {string} elementId - player container ID
     * @param {object} meta - { title, artist, album, artworkUrl, prevAvailable, nextAvailable }
     * @param {object} handlers - { prev, next } optional .NET DotNetObjectReference
     *                             with InvokePrev/InvokeNext methods
     */
    function setMediaSession(elementId, meta, handlers) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.art) return;
        try {
            if (!('mediaSession' in navigator)) return;
            const ms = navigator.mediaSession;
            const artwork = [];
            if (meta && meta.artworkUrl) {
                // Provide a few size hints — OS picks the closest match.
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
            // Wire prev/next only if .NET says the episode list permits it,
            // otherwise the lock-screen buttons appear dead.
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

    /**
     * Toggle Picture-in-Picture on the active player video. Returns a promise
     * that resolves true if we entered PiP, false otherwise.
     */
    async function togglePictureInPicture(elementId) {
        const entry = WIRED.get(elementId);
        if (!entry || !entry.art || !entry.art.video) return false;
        const video = entry.art.video;
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

    window.animarrPlayer = { attach, flush, detach, setMediaSession, togglePictureInPicture };
})();
