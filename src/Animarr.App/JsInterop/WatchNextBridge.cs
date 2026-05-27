using Animarr.App.Services;
using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

/// <summary>
/// JS-callable surface for <see cref="WatchNextService"/>.
///
/// The Blazor / Artplayer side (animarr-player.js) calls these methods
/// fire-and-forget on every progress tick + on pause / ended. The bridge
/// dispatches to the singleton WatchNextService which decides whether the
/// platform supports Watch Next (Android TV only); non-Android platforms
/// turn the calls into a no-op, so the JS side doesn't need to feature-detect.
///
/// All methods catch their own exceptions so a JS interop failure can never
/// surface as a player error. The expected error rate is "rare" — the worst
/// case is a row failing to publish, which just means the launcher doesn't
/// show the card this session.
/// </summary>
public static class WatchNextBridge
{
    /// <summary>
    /// Insert or update the Watch Next card for this media item. Called from
    /// JS on play-start (positionMs = current resume seconds) and on every
    /// progress tick (~5s cadence) + on pause / ended.
    /// </summary>
    [JSInvokable("WatchNextUpsert")]
    public static async Task WatchNextUpsert(string mediaId, string title, string posterUrl,
                                              long positionMs, long durationMs)
    {
        var svc = WatchNextService.Instance;
        if (svc is null) return;
        try { await svc.UpsertAsync(mediaId, title, posterUrl, positionMs, durationMs); }
        catch { /* swallow — bridge can't throw back to JS without breaking the player */ }
    }

    /// <summary>
    /// Remove the Watch Next card for this media item. Called when the user
    /// marks the title fully watched or deletes it from Animarr.
    /// </summary>
    [JSInvokable("WatchNextRemove")]
    public static async Task WatchNextRemove(string mediaId)
    {
        var svc = WatchNextService.Instance;
        if (svc is null) return;
        try { await svc.RemoveAsync(mediaId); }
        catch { /* swallow — same reason as Upsert */ }
    }
}
