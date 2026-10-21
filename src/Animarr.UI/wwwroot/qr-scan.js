// v5 Phase 7 — TV pairing QR scanner.
//
// Thin wrapper around html5-qrcode, loaded on first scan (below) — it used to
// be a parser-blocking CDN <script> in both index.html hosts, paid for on every
// page load although only the Login / Pair QR modal needs it.
// Exposes two globals the Login.razor page calls via JSInterop:
//
//   animarr_startQrScan(elementId, dotnetRef)
//     Opens the camera in the element and starts scanning. On a successful
//     decode it calls dotnetRef.invokeMethodAsync("OnQrDecoded", payload).
//     Stops the scanner immediately after the first hit so the user can't
//     accidentally double-confirm.
//
//   animarr_stopQrScan()
//     Tears down any active scanner — safe to call when nothing is running
//     (no-op). Login.razor calls this on modal close.
//
// Single instance — only one scanner active at a time. html5-qrcode keeps
// the MediaStream alive across re-starts so we must stop() before init.

(function () {
    let _scanner = null;
    let _active  = false;

    // Pinned version + SRI, same as the old index.html tag.
    const LIB_SRC = "https://cdn.jsdelivr.net/npm/html5-qrcode@2.3.8/html5-qrcode.min.js";
    const LIB_SRI = "sha384-c9d8RFSL+u3exBOJ4Yp3HUJXS4znl9f+z66d1y54ig+ea249SpqR+w1wyvXz/lk+";
    let _libLoad = null;   // in-flight / finished load; reset on failure so a retry refetches

    function loadLib() {
        if (typeof Html5Qrcode !== "undefined") return Promise.resolve();
        if (_libLoad) return _libLoad;
        _libLoad = new Promise(function (resolve, reject) {
            const s = document.createElement("script");
            s.src = LIB_SRC;
            s.integrity = LIB_SRI;
            s.crossOrigin = "anonymous";
            s.onload = function () { resolve(); };
            s.onerror = function () {
                _libLoad = null;
                s.remove();
                reject(new Error("html5-qrcode failed to load"));
            };
            document.head.appendChild(s);
        });
        return _libLoad;
    }

    window.animarr_startQrScan = async function (elementId, dotnetRef) {
        try { await loadLib(); } catch (e) { console.warn("[animarr_startQrScan]", e); }
        if (typeof Html5Qrcode === "undefined") {
            console.warn("[animarr_startQrScan] html5-qrcode not loaded (CDN unreachable?)");
            return;
        }
        // The modal may have closed while the library loaded.
        if (!document.getElementById(elementId)) return;
        await window.animarr_stopQrScan();
        _scanner = new Html5Qrcode(elementId, { verbose: false });
        _active  = true;

        const onSuccess = async (decoded) => {
            // Stop first — the .NET callback may await network, and we don't
            // want the camera to keep firing decode events in the meantime.
            try { await window.animarr_stopQrScan(); } catch (_) {}
            try { await dotnetRef.invokeMethodAsync("OnQrDecoded", decoded); }
            catch (e) { console.error("[animarr_startQrScan] dotnet callback failed", e); }
        };
        const onError = (_msg) => { /* per-frame; ignore */ };

        try {
            // No qrbox: html5-qrcode draws its shaded viewfinder for the
            // video's own aspect, but the page crops the video to a square
            // (object-fit: cover), so the clear window came out a flat
            // rectangle. PairConfirm draws a square one in CSS instead, and
            // the whole frame is decoded.
            await _scanner.start(
                { facingMode: "environment" },  // prefer rear camera on phones
                { fps: 10 },
                onSuccess,
                onError);
        } catch (e) {
            console.warn("[animarr_startQrScan] start failed", e);
            _active = false;
            _scanner = null;
            throw e;
        }
    };

    window.animarr_stopQrScan = async function () {
        if (!_scanner || !_active) { _scanner = null; _active = false; return; }
        try { await _scanner.stop(); } catch (_) {}
        try { _scanner.clear(); } catch (_) {}
        _scanner = null;
        _active  = false;
    };
})();
