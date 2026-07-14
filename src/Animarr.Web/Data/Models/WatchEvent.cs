namespace Animarr.Web.Data.Models;

/// <summary>
/// Append-mostly journal of playback activity, one row per
/// (user, item, season, episode, calendar day). Powers the future stats
/// surface (watch-time heatmap, streaks, hours-per-month) which the
/// current-state-only <see cref="WatchState"/> can't answer — it has no
/// notion of WHEN the watching happened beyond LastSeenAt.
///
/// Rows are upserted by <c>WatchEventRecorder</c> from both progress paths
/// (browser player REST endpoint and the external mpv tracker); the day
/// bucket keeps growth trivial (a heavy binger writes ~20 rows/day).
/// </summary>
public class WatchEvent
{
    public Guid Id { get; set; }

    /// <summary>FK → User.Id. Null for pings from the anonymous external-player
    /// path (mpv tracker), mirroring the UserId=null convention of its
    /// WatchState rows. SetNull on user delete so history survives.</summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>FK → MediaItem.Id. Cascade-deleted with the item (consistent
    /// with WatchState — stats slightly under-count removed titles).</summary>
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Season number for series episodes; NULL for movies.</summary>
    public int? Season { get; set; }

    /// <summary>Episode number within the season; NULL for movies.</summary>
    public int? Episode { get; set; }

    /// <summary>UTC calendar day the activity belongs to (time part is 00:00).
    /// Day granularity is deliberate: compact, and precise enough for
    /// heatmaps/streaks without logging exact viewing times.</summary>
    public DateTime Date { get; set; }

    /// <summary>Seconds actually played on this day for this episode —
    /// accumulated from per-ping position deltas, seeks excluded.</summary>
    public long SecondsWatched { get; set; }

    public DateTime UpdatedAt { get; set; }
}
