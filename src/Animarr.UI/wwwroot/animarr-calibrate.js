// Animarr audio-sync calibrator.
//
// Runs once per browser/device on the first playback attempt and stashes
// the result in localStorage. Subsequent /api/hls/start calls pass the
// stored offset via ?audioOffsetMs= so ffmpeg's -itsoffset compensates
// for the browser's playback-chain latency.
//
// How calibration works:
//   • /calibration.mp4 is a 2-second clip that flashes a white square
//     for ~50ms at t = 0, 0.5, 1.0, 1.5s — and emits a 1 kHz tick at
//     the exact same instants. Source content is perfectly A/V-aligned.
//   • We play it in a hidden <video>, sample audio amplitude through
//     a WebAudio AnalyserNode AND video pixel brightness through a
//     1×1 canvas every animation frame.
//   • Whenever brightness crosses a high threshold we log a "video
//     event" wall-clock time. Whenever audio amplitude crosses a
//     high threshold we log an "audio event".
//   • Pair the events (i-th video flash with i-th audio tick) and
//     average `audio_time - video_time`. That delta is the browser's
//     audio chain latency relative to its video chain.
//   • Server offset = -delta. Audio that would arrive `delta` ms behind
//     video at the user's ears gets shifted `delta` ms earlier in the
//     encoded stream → they meet at the speaker/screen.
//
// Caveats:
//   • Calibration MP4 is plain H.264; HEVC re-encoded HLS may have
//     slightly different decoder latency, so we expect ±15ms accuracy.
//   • Requires audio context to be unlocked → must be called from
//     inside a user gesture handler. The player overlay's "Play" click
//     is that gesture.
//   • If the measurement fails (browser blocks, fewer than 2 event
//     pairs captured) we fall back to a 40 ms baseline that empirically
//     centres the residual on the safer "audio-late" side.

(function () {
    // ─── Two-channel offset storage ────────────────────────────────────────
    // Hardware-encoded path (VAAPI re-encode, -bf 0) and software stream-copy
    // path (HEVC 10-bit / HDR / DV) have FUNDAMENTALLY different A/V sync
    // characteristics:
    //
    //   • HW: B-frames stripped via -bf 0, sync is ~0ms baseline. Audio-late
    //     by ~50-80ms once browser/decoder chain latency is added.
    //     → typical needed offset: +70ms.
    //
    //   • SW (stream-copy): B-frames preserved, fMP4 TFDT carries first
    //     decode-time instead of first display-time. Net result: audio
    //     reaches the user ~50ms BEFORE video.
    //     → typical needed offset: ALSO positive (+50ms), but the residual
    //     after correction lands differently than HW because of B-frame
    //     reorder.
    //
    // One slider can't tune both correctly. Two separate values, two storage
    // keys, two slider widgets in Settings.
    const STORAGE_KEY_HW = 'animarr_audio_offset_hw_ms';   // VAAPI re-encode path
    const STORAGE_KEY_SW = 'animarr_audio_offset_sw_ms';   // fMP4 stream-copy path
    const LEGACY_KEY     = 'animarr_audio_offset_ms';      // pre-split, migrate from this once
    const DEFAULT_OFFSET_MS = 70;

    /** Returns cached calibration result for a channel (`'hw'` or `'sw'`),
     *  or null if never calibrated/set. */
    function getCached(channel) {
        const key = channel === 'sw' ? STORAGE_KEY_SW : STORAGE_KEY_HW;
        try {
            let v = localStorage.getItem(key);
            // Migration: if the new channel-specific key isn't set but the
            // legacy single key is, adopt it for HW (the old single value
            // was implicitly the hardware-encoded one).
            if (v === null && channel !== 'sw') {
                v = localStorage.getItem(LEGACY_KEY);
                if (v !== null) {
                    try { localStorage.setItem(STORAGE_KEY_HW, v); } catch { }
                }
            }
            if (v === null) return null;
            const n = parseInt(v, 10);
            return Number.isFinite(n) ? n : null;
        } catch { return null; }
    }

    /** Force a fresh auto-calibration on next playback. Only HW channel
     *  actually has auto-calibration — SW is manual-only (the calibration
     *  MP4 is software-decoded, not representative of the stream-copy path). */
    function clearCache(channel) {
        const key = channel === 'sw' ? STORAGE_KEY_SW : STORAGE_KEY_HW;
        try { localStorage.removeItem(key); } catch { }
        if (channel !== 'sw') {
            try { localStorage.removeItem(LEGACY_KEY); } catch { }
        }
    }

    function saveCache(channel, ms) {
        const key = channel === 'sw' ? STORAGE_KEY_SW : STORAGE_KEY_HW;
        try { localStorage.setItem(key, String(Math.round(ms))); } catch { }
    }

    /** Manual override from a Settings slider. `channel` is `'hw'` or `'sw'`. */
    function setManual(channel, ms) { saveCache(channel, ms); }

    /**
     * Run a one-off calibration pass.
     * @returns {Promise<number>} offset in milliseconds (positive = shift audio LATER in stream)
     */
    /** Auto-calibrate the HW channel only — the calibration test clip is
     *  software-decoded H.264, so the measured offset characterises the
     *  browser's HW playback chain (= VAAPI re-encode path on server).
     *  SW channel stays at whatever the user set or defaults. */
    async function calibrate() {
        const cached = getCached('hw');
        if (cached !== null) return cached;

        try {
            const measured = await measure();
            // Clamp to a sane envelope. A real browser delay should be in
            // ±150ms; anything past that is a measurement error and we
            // shouldn't trust it as a permanent setting.
            const clamped = Math.max(-200, Math.min(200, Math.round(measured)));
            saveCache('hw', clamped);
            console.info('animarr: audio-sync calibrated to', clamped, 'ms (hw channel)');
            return clamped;
        } catch (err) {
            console.warn('animarr: calibration failed, using default', err);
            saveCache('hw', DEFAULT_OFFSET_MS);
            return DEFAULT_OFFSET_MS;
        }
    }

    async function measure() {
        const video = document.createElement('video');
        // Shipped as a static web asset from Animarr.UI's wwwroot so both
        // standalone WASM and the MAUI hybrid find it at the same path.
        video.src = '_content/Animarr.UI/calibration.mp4';
        video.crossOrigin = 'anonymous';
        video.muted = false;
        // Full amplitude through the WebAudio graph — the muteGain below
        // silences the speaker output anyway, but the AnalyserNode taps the
        // signal BEFORE that gain so it sees the un-attenuated waveform.
        // Previous 0.25 caused audio amplitude to never cross the detection
        // threshold (every calibration ended with 0 audio events).
        video.volume = 1.0;
        video.playsInline = true;
        video.preload = 'auto';
        // Off-screen but present in DOM so the browser actually decodes it.
        video.style.cssText = 'position:fixed;left:-9999px;top:-9999px;width:32px;height:24px;pointer-events:none;';
        document.body.appendChild(video);

        const canvas = document.createElement('canvas');
        canvas.width = 16; canvas.height = 12;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });

        // Wait for the video to be ready, then unlock audio context and play.
        await new Promise((res, rej) => {
            video.addEventListener('canplay', res, { once: true });
            video.addEventListener('error', () => rej(new Error('video load failed')), { once: true });
            setTimeout(() => rej(new Error('video load timeout')), 4000);
        });

        const AudioCtor = window.AudioContext || window.webkitAudioContext;
        if (!AudioCtor) throw new Error('no audio context');
        const audioCtx = new AudioCtor();
        if (audioCtx.state === 'suspended') {
            try { await audioCtx.resume(); } catch { }
        }
        const source = audioCtx.createMediaElementSource(video);
        const analyser = audioCtx.createAnalyser();
        analyser.fftSize = 256;
        analyser.smoothingTimeConstant = 0;
        const audioBuf = new Uint8Array(analyser.fftSize);
        source.connect(analyser);
        // Route to silence so we don't blast the user (analyser already taps
        // before destination); we want the audio path to actually run though.
        const muteGain = audioCtx.createGain();
        muteGain.gain.value = 0.001;
        analyser.connect(muteGain);
        muteGain.connect(audioCtx.destination);

        const videoEvents = [];
        const audioEvents = [];
        const COOLDOWN_MS = 250;   // expect events every 500ms
        const VIDEO_THRESHOLD = 180; // 0-255 luminance
        const AUDIO_THRESHOLD = 20;  // 0-128 amplitude delta from midpoint
        let lastVideoEvt = -1000;
        let lastAudioEvt = -1000;
        let stopped = false;
        let maxObservedPeak = 0;  // diagnostic: helps tune AUDIO_THRESHOLD per browser

        function tick() {
            if (stopped) return;
            const now = performance.now();

            // Sample video brightness
            try {
                ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
                let sum = 0;
                for (let i = 0; i < data.length; i += 4) sum += data[i]; // R channel
                const avg = sum / (data.length / 4);
                if (avg > VIDEO_THRESHOLD && now - lastVideoEvt > COOLDOWN_MS) {
                    videoEvents.push(now);
                    lastVideoEvt = now;
                }
            } catch { /* drawImage can throw before first frame */ }

            // Sample audio amplitude
            analyser.getByteTimeDomainData(audioBuf);
            let peak = 0;
            for (let i = 0; i < audioBuf.length; i++) {
                const v = Math.abs(audioBuf[i] - 128);
                if (v > peak) peak = v;
            }
            if (peak > maxObservedPeak) maxObservedPeak = peak;
            if (peak > AUDIO_THRESHOLD && now - lastAudioEvt > COOLDOWN_MS) {
                audioEvents.push(now);
                lastAudioEvt = now;
            }

            requestAnimationFrame(tick);
        }
        requestAnimationFrame(tick);

        await new Promise((res, rej) => {
            video.addEventListener('ended', res, { once: true });
            // Hard cap — calibration clip is 2s, allow 5s for slow decoders.
            setTimeout(() => rej(new Error('calibration timeout')), 5000);
            video.play().catch(rej);
        });
        stopped = true;

        // Cleanup
        try { audioCtx.close(); } catch { }
        try { video.pause(); video.removeAttribute('src'); video.load(); } catch { }
        try { document.body.removeChild(video); } catch { }

        // AnalyserNode reads audio BEFORE it goes through the OS audio
        // driver + DAC + speakers. The real "this sample is now audible"
        // event lags the analyser detection by `outputLatency` seconds.
        // The video side has no analogous delay (canvas reads what's
        // currently being composited, ≈ what's on screen). So observed
        // delta UNDER-estimates the real audio-vs-video offset by
        // outputLatency. Add it back into the equation.
        const outputLatencyMs = (audioCtx.outputLatency || audioCtx.baseLatency || 0) * 1000;

        // Diagnostic — surfaced in console so failures can be tuned without
        // editing the script. If maxObservedPeak < 10, the analyser never
        // saw audio data → likely a CORS / autoplay / source-routing issue,
        // not a threshold problem.
        console.debug('animarr-calibrate:', {
            videoEvents: videoEvents.length,
            audioEvents: audioEvents.length,
            maxObservedPeak,
            outputLatencyMs: Math.round(outputLatencyMs),
            videoAt: videoEvents.map(t => Math.round(t)),
            audioAt: audioEvents.map(t => Math.round(t)),
        });
        const pairs = Math.min(videoEvents.length, audioEvents.length);
        if (pairs < 2) {
            throw new Error(`not enough events: video=${videoEvents.length} audio=${audioEvents.length} maxPeak=${maxObservedPeak}`);
        }
        // Drop the very first pair — first audio frame after AudioContext
        // unlock often has a one-off latency spike that biases the average.
        const start = pairs >= 3 ? 1 : 0;
        let sum = 0, count = 0;
        for (let i = start; i < pairs; i++) {
            sum += audioEvents[i] - videoEvents[i];
            count++;
        }
        const avgDelta = sum / count;
        // True audio-vs-video offset at the user's ears/eyes:
        //   real_delta = (analyser_seen_audio_time + outputLatency) - canvas_seen_video_time
        //              = avgDelta + outputLatencyMs
        // To make real_delta → 0, push audio LATER in the stream by that much.
        const realDelta = avgDelta + outputLatencyMs;
        return -realDelta;
    }

    window.animarrCalibrate = { calibrate, getCached, clearCache, setManual };
})();
