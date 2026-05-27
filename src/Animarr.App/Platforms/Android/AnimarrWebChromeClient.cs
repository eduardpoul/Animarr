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

        // Grant any of the camera / mic resources the page asks for. We're
        // a single-purpose self-hosted media app — there's nothing the user
        // could be tricked into authorising that they didn't already opt
        // into by installing Animarr. The corresponding Android permission
        // (CAMERA) is still required at the manifest level; if it's missing
        // the system blocks the WebView before we even reach this callback.
        var requested = request.GetResources() ?? Array.Empty<string>();
        var granted = new System.Collections.Generic.List<string>();
        foreach (var r in requested)
        {
            if (r == PermissionRequest.ResourceVideoCapture ||
                r == PermissionRequest.ResourceAudioCapture)
            {
                granted.Add(r);
            }
        }
        if (granted.Count > 0)
        {
            try { request.Grant(granted.ToArray()); }
            catch { request.Deny(); }
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
