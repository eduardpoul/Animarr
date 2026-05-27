using Animarr.App.Services;
using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

// Re-exported here so the JSInterop layer's serialiser picks the type up
// when shuttling NativePlayerState across the WebView IPC boundary.

/// <summary>
/// JS-callable surface for <see cref="NativePlayerService"/>. Mirrors the
/// abstract player adapter contract in animarr-player.js; the JS side wraps
/// these in a <c>NativeAdapter</c> instance that the HUD talks to (instead
/// of the Artplayer-backed adapter) when running on a host where
/// <see cref="NativePlayerService.IsAvailable"/> returns true.
///
/// Phase 2 (2026-05-27): the methods below construct an ExoPlayer but don't
/// render to a visible Surface yet — audio plays, video doesn't. A later
/// iteration adds a custom Handler that lays a PlayerView behind the
/// BlazorWebView and wires <see cref="NativePlayerService"/> to it.
///
/// All exceptions swallowed — bridge failures must never crash the player
/// UI. The JS side falls back to ArtplayerAdapter when IsAvailable() is
/// false, so a no-op here just keeps everyone on the existing path.
/// </summary>
public static class NativePlayerBridge
{
    /// <summary>
    /// JS-side gate inside <c>window.animarrNativePlayer.isAvailable()</c>.
    /// True only on Android TV (FEATURE_LEANBACK) builds with the Media3
    /// packages on the classpath; everything else returns false so the
    /// WebView+Artplayer path stays in use.
    /// </summary>
    [JSInvokable("NativePlayerIsAvailable")]
    public static bool IsAvailable()
        => NativePlayerService.Instance?.IsAvailable ?? false;

    /// <summary>
    /// Start playback. `url` is whatever the HLS-start response handed us —
    /// either a Direct Play <c>/api/file?path=…</c> URL or an HLS manifest
    /// URL. ExoPlayer auto-detects format from the URL extension.
    /// </summary>
    [JSInvokable("NativePlayerPlay")]
    public static async Task PlayAsync(string url, long resumeMs)
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.PlayAsync(url, resumeMs); }
        catch { /* swallow */ }
    }

    [JSInvokable("NativePlayerPause")]
    public static async Task PauseAsync()
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.PauseAsync(); }
        catch { /* swallow */ }
    }

    [JSInvokable("NativePlayerResume")]
    public static async Task ResumeAsync()
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.ResumeAsync(); }
        catch { /* swallow */ }
    }

    [JSInvokable("NativePlayerSeek")]
    public static async Task SeekAsync(long positionMs)
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.SeekAsync(positionMs); }
        catch { /* swallow */ }
    }

    [JSInvokable("NativePlayerDetach")]
    public static async Task DetachAsync()
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.DetachAsync(); }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Polled by the JS NativeAdapter every ~250ms while playback is active —
    /// drives the HUD progressbar, time labels, and play/pause icon. Returns
    /// the same record every time (never null) so the JS side doesn't need
    /// null-checks on the hot path.
    /// </summary>
    [JSInvokable("NativePlayerGetState")]
    public static NativePlayerState GetState()
    {
        var svc = NativePlayerService.Instance;
        return svc?.GetState() ?? NativePlayerState.Empty;
    }

    /// <summary>Set player volume on 0..1 scale. Mute = volume 0.</summary>
    [JSInvokable("NativePlayerSetVolume")]
    public static async Task SetVolumeAsync(float volume)
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.SetVolumeAsync(volume); }
        catch { /* swallow */ }
    }

    /// <summary>Switch the sideloaded subtitle. Pass null URL to disable.</summary>
    [JSInvokable("NativePlayerSetSubtitle")]
    public static async Task SetSubtitleAsync(string? url, string? language)
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.SetSubtitleAsync(url, language); }
        catch { /* swallow */ }
    }

    /// <summary>Apply aspect-ratio crop via TextureView matrix transform.
    /// Values: "default" / "21:9" / "16:9" / "4:3" / "2.35:1".</summary>
    [JSInvokable("NativePlayerSetAspect")]
    public static async Task SetAspectRatioAsync(string value)
    {
        var svc = NativePlayerService.Instance;
        if (svc is null) return;
        try { await svc.SetAspectRatioAsync(value ?? "default"); }
        catch { /* swallow */ }
    }

    /// <summary>Probe the device's MediaCodec for whether it can decode the
    /// given format. JS calls this before committing to the native path
    /// when the codec is anything other than baseline H.264 — saves users
    /// from a black-screen experience on TV boxes that lie about Main10.</summary>
    [JSInvokable("NativePlayerCanDecode")]
    public static bool CanDecode(string codec, int bitDepth, string? hdr, int width, int height)
        => NativePlayerService.Instance?.CanDecode(codec, bitDepth, hdr, width, height) ?? true;
}
