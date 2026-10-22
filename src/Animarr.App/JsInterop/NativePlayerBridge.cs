using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

/// <summary>
/// Phone / tablet hand-off from the WebView's "Play" to the native player
/// (<see cref="PlayerPage"/> in its touch mode). The web page calls
/// <c>window.animarrNativePlay</c> (index.html) with the file it was about to
/// play; the native page goes up over the WebView and the call resolves when
/// it is done:
/// <list type="bullet">
///   <item>"closed" — it played and was closed: the page refreshes its watch
///         states (the native player recorded the progress);</item>
///   <item>"failed" — it could not start the stream: the web player plays the
///         file instead (the automatic fallback);</item>
///   <item>"web" — the device plays in the web player (its setting, or "Open
///         in the web player" from the native menu), or there is no native
///         player here (TV shell, non-Android).</item>
/// </list>
/// </summary>
public static class NativePlayerBridge
{
    /// <summary>The device's player setting: "native" | "web" (get with an
    /// empty <paramref name="set"/>), or "" where no native phone player
    /// exists — the web settings card hides itself then.</summary>
    [JSInvokable("NativePlayerMode")]
    public static string NativePlayerMode(string? set)
    {
#if ANDROID
        if (DeviceKind.IsTv) return "";
        if (set is "native" or "web") Services.TvPlaybackPrefs.PhonePlayer = set;
        return Services.TvPlaybackPrefs.PhonePlayer;
#else
        return "";
#endif
    }

    [JSInvokable("NativePlay")]
    public static async Task<string> NativePlayAsync(string mediaItemId, string path, int? season, int? episode, string? title)
    {
#if ANDROID
        if (DeviceKind.IsTv || Services.TvPlaybackPrefs.PhonePlayer != "native") return "web";
        if (!Guid.TryParse(mediaItemId, out var id) || string.IsNullOrEmpty(path)) return "web";
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } w ? w[0].Page : null;
                if (host is null) return "web";
                // A second Play while one is up (a double tap that got through).
                if (PlayerPage.Current is not null) return "closed";
                var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Resume -1: the page looks the file's saved position up itself.
                var page = new PlayerPage(id, season, episode, path, title, resumeMs: -1) { Outcome = outcome };
                await host.Navigation.PushModalAsync(page, animated: false);
                return await outcome.Task;
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.Player", $"native hand-off failed: {ex.Message}");
            return "failed";
        }
#else
        await Task.CompletedTask;
        return "web";
#endif
    }
}
