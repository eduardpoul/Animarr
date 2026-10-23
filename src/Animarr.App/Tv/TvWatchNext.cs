#if ANDROID
using System.Collections.Concurrent;
using Animarr.App.Services;
using Animarr.Shared.Models;

namespace Animarr.App.Tv;

/// <summary>
/// Feeds the launcher's "Continue watching" row (<see cref="WatchNextService"/>)
/// from the signed-in profile's Continue + Next up — the list the TV home
/// shows. Published whenever the home loads or refreshes it, and when the app
/// goes to the background (so a card follows what was just watched). A failed
/// fetch leaves the cards as they are; only an answered, empty list clears them.
/// </summary>
internal static class TvWatchNext
{
    /// <summary>A profile is signed in on the TV home (set by it, cleared on
    /// sign-out) — the background refresh asks the server only then.</summary>
    public static volatile bool Active;

    // Episode titles per title, for this app session.
    private static readonly ConcurrentDictionary<Guid, EpisodeMetaDto[]> s_meta = new();
    private static int s_pending;

    /// <summary>The home's freshly loaded lists → the cards (in the background).</summary>
    public static void Publish(TvData data, TvData.ContinueItem[]? cont, TvData.ContinueItem[]? next)
    {
        Active = true;
        if (cont is null && next is null) return;
        _ = Task.Run(() => PublishAsync(data, cont, next));
    }

    /// <summary>Re-read the lists and publish (the app left the screen).</summary>
    public static void RefreshSoon()
    {
        if (!Active || Interlocked.Exchange(ref s_pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));   // the player's last progress save lands first
                var data = new TvData();
                await TvMedia.EnsureTokenAsync();
                var cont = await data.Get<TvData.ContinueItem[]>("/api/me/continue?take=12");
                var next = await data.Get<TvData.ContinueItem[]>("/api/me/next-up?take=12");
                if (cont is null && next is null) return;
                await PublishAsync(data, cont, next);
            }
            catch (Exception ex) { Android.Util.Log.Warn("AnimarrTv", $"watch next refresh: {ex.Message}"); }
            finally { Interlocked.Exchange(ref s_pending, 0); }
        });
    }

    /// <summary>Signed out: nobody's list stays on the home screen.</summary>
    public static Task ClearAsync()
    {
        Active = false;
        return WatchNextService.Instance?.ClearAsync() ?? Task.CompletedTask;
    }

    private static async Task PublishAsync(TvData data, TvData.ContinueItem[]? cont, TvData.ContinueItem[]? next)
    {
        if (WatchNextService.Instance is not { } svc) return;
        try
        {
            // Continue first, then Next up; one card per title (the home's order).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = (cont ?? Array.Empty<TvData.ContinueItem>())
                .Concat(next ?? Array.Empty<TvData.ContinueItem>())
                .Where(i => seen.Add(i.MediaItemId))
                .Take(WatchNextService.MaxCards)
                .ToList();

            var now = DateTime.UtcNow;
            var cards = new List<WatchNextService.Card>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (!Guid.TryParse(c.MediaItemId, out var id)) continue;
                var images = new List<string>();
                if (c.Season is int s && c.Episode is int e) images.Add(data.EpisodeThumb(c.MediaItemId, s, e));
                else if (c.Episode is int abs) images.Add(data.EpisodeThumb(c.MediaItemId, 1, abs));
                images.Add(data.Img(c.FanartPath ?? c.PosterPath, 640));
                if (c.FanartPath is not null && c.PosterPath is not null) images.Add(data.Img(c.PosterPath, 640));

                long duration = c.RuntimeMs ?? 0;
                long position = c.ProgressMs ?? (duration > 0 ? (long)(c.Progress * duration) : 0);
                // The server's last-seen time orders the row; else keep the list's order.
                var seenAt = c.LastSeenAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : now.AddMinutes(-i);
                cards.Add(new WatchNextService.Card(
                    id, c.Title ?? "", c.Season, c.Episode, c.Episode is int ep ? await EpisodeTitleAsync(data, id, c.Season, ep) : null,
                    c.IsNew, position, duration, seenAt, images));
            }
            await svc.SyncAsync(cards, data.Http);
        }
        catch (Exception ex) { Android.Util.Log.Warn("AnimarrTv", $"watch next publish: {ex.Message}"); }
    }

    private static async Task<string?> EpisodeTitleAsync(TvData data, Guid id, int? season, int episode)
    {
        if (!s_meta.TryGetValue(id, out var metas))
        {
            metas = await TvData.Safe(data.Api.GetEpisodeMetadataAsync(id)) ?? Array.Empty<EpisodeMetaDto>();
            s_meta[id] = metas;
        }
        return (metas.FirstOrDefault(m => m.Season == (season ?? 1) && m.Episode == episode)
                ?? metas.FirstOrDefault(m => m.Season == 1 && m.Episode == episode))?.Title;
    }
}
#endif
