#if ANDROID
using Android.Webkit;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Animarr.App.Platforms.Android;

/// <summary>
/// BlazorWebView on Android wires up its own <see cref="WebChromeClient"/>
/// internally (one that handles JS dialogs and console messages). By default
/// that client does NOT call <see cref="PermissionRequest.Grant(string[])"/>
/// for <see cref="PermissionRequest.ResourceVideoCapture"/>, so any
/// <c>navigator.mediaDevices.getUserMedia({ video: true })</c> from Blazor
/// fails with <c>NotAllowedError: Permission denied</c> even when the host
/// activity already has the Android <c>CAMERA</c> permission.
///
/// We wrap that internal client (kept around via <see cref="_inner"/> so
/// dialogs / console / progress callbacks still work) and forward everything
/// untouched except <see cref="OnPermissionRequest"/> + the audio/video-capture
/// permissions, which we grant automatically. The phone-side QR scanner in
/// <c>PairConfirm.razor</c> relies on this to open the camera.
///
/// Installed via a <see cref="BlazorWebViewHandler"/> mapper hook in
/// MauiProgram so it picks up the BlazorWebView created for every page —
/// no per-page wiring needed.
/// </summary>
internal sealed class AnimarrWebChromeClient : WebChromeClient
{
    private readonly WebChromeClient? _inner;

    public AnimarrWebChromeClient(WebChromeClient? inner)
    {
        _inner = inner;
    }

    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null) { _inner?.OnPermissionRequest(request); return; }

        // The camera (the pairing QR scanner) and only for the app's own page
        // (the BlazorWebView virtual host https://0.0.0.x): an embedded frame
        // (a trailer, a provider page) asking for the camera, or anyone asking
        // for the microphone, is denied here — the inner client would grant
        // whatever the app holds. The Android CAMERA permission is still
        // needed at the manifest / runtime level.
        var requested = request.GetResources() ?? Array.Empty<string>();
        var granted = new System.Collections.Generic.List<string>();
        bool ownPage = request.Origin?.Host?.StartsWith("0.0.0.", StringComparison.Ordinal) == true;
        foreach (var r in requested)
        {
            if (ownPage && r == PermissionRequest.ResourceVideoCapture)
                granted.Add(r);
        }
        if (granted.Count > 0)
        {
            try { request.Grant(granted.ToArray()); }
            catch { request.Deny(); }
            return;
        }
        if (requested.Any(r => r == PermissionRequest.ResourceVideoCapture || r == PermissionRequest.ResourceAudioCapture))
        {
            request.Deny();
            return;
        }

        // Anything else (geolocation, midi, protected media) goes through the
        // inner client's default handling.
        _inner?.OnPermissionRequest(request);
    }

    // Forward the noisy callbacks to the inner client so Blazor's dev-tools
    // logs + dialog handling still work. We could implement everything here,
    // but mirroring what BlazorWebView already does keeps the contract intact
    // across future MAUI versions.
    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
        => _inner?.OnConsoleMessage(consoleMessage) ?? base.OnConsoleMessage(consoleMessage);

    public override bool OnJsAlert(global::Android.Webkit.WebView? view, string? url, string? message, JsResult? result)
        => _inner?.OnJsAlert(view, url, message, result) ?? base.OnJsAlert(view, url, message, result);

    public override bool OnJsConfirm(global::Android.Webkit.WebView? view, string? url, string? message, JsResult? result)
        => _inner?.OnJsConfirm(view, url, message, result) ?? base.OnJsConfirm(view, url, message, result);
}
#endif
