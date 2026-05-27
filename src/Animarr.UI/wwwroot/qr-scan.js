// v5 Phase 7 — TV pairing QR scanner.
//
// Thin wrapper around html5-qrcode (loaded via <script> tag in index.html).
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

    window.animarr_startQrScan = async function (elementId, dotnetRef) {
        if (typeof Html5Qrcode === "undefined") {
            console.warn("[animarr_startQrScan] html5-qrcode not loaded — check <script> tag in index.html");
            return;
        }
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
            await _scanner.start(
                { facingMode: "environment" },  // prefer rear camera on phones
                { fps: 10, qrbox: { width: 240, height: 240 } },
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
