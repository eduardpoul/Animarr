using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Upserts <see cref="WatchEvent"/> day-bucket rows. Called from both progress
/// paths (WatchStateEndpoints for the browser player, WatchStateService for the
/// external mpv tracker) inside THEIR db context — no SaveChanges here, so the
/// event lands in the same transaction as the WatchState update.
/// </summary>
internal static class WatchEventRecorder
{
    /// <summary>Max seconds a single progress ping may credit. Browser pings
    /// arrive every ~2-5s during playback, mpv every 5s — anything larger is a
    /// seek or a long-stalled tab and must not count as watch time. Mirrors the
    /// clamp on /api/watch/external-progress.</summary>
    public const int MaxTickSeconds = 30;

    public static async Task RecordAsync(
        AppDbContext db, Guid? userId, Guid mediaItemId, int? season, int? episode,
        long secondsWatched, DateTime nowUtc, CancellationToken ct)
    {
        if (secondsWatched <= 0) return;

        var day = nowUtc.Date;
        var row = await db.WatchEvents.FirstOrDefaultAsync(e =>
            e.UserId      == userId &&
            e.MediaItemId == mediaItemId &&
            e.Season      == season &&
            e.Episode     == episode &&
            e.Date        == day, ct);
        if (row is null)
        {
            db.WatchEvents.Add(new WatchEvent
            {
                Id             = Guid.NewGuid(),
                UserId         = userId,
                MediaItemId    = mediaItemId,
                Season         = season,
                Episode        = episode,
                Date           = day,
                SecondsWatched = secondsWatched,
                UpdatedAt      = nowUtc,
            });
        }
        else
        {
            row.SecondsWatched += secondsWatched;
            row.UpdatedAt       = nowUtc;
        }
    }
}
