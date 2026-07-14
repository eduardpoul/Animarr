using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Persists per-file watch progress, exposes resolver helpers for the
/// "Continue · …" CTA, and aggregates totals for the activity surface.
///
/// All write methods fire <see cref="WatchStateChanged"/> with the affected
/// MediaItemId so UI subscribers (Catalog grid, MediaDetail hero / episode
/// list, sidebar progress) can refresh without polling.
///
/// Spec: design_handoff_animarr/CHANGELOG_FOR_CLAUDE_CODE.md §1 + §2.
/// </summary>
public interface IWatchStateService
{
    Task<WatchState?> GetForMovieAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<WatchState?> GetForEpisodeAsync(Guid mediaItemId, int season, int episode, CancellationToken ct = default);
    Task<IReadOnlyList<WatchState>> GetForSeriesAsync(Guid mediaItemId, CancellationToken ct = default);

    /// <summary>Returns (watched, total) episode counts for a series. `total`
    /// is the on-disk available count (caller computes from seasons), so this
    /// method only resolves the `watched` half — pass total back through unchanged.</summary>
    Task<int> CountWatchedEpisodesAsync(Guid mediaItemId, CancellationToken ct = default);

    Task MarkMovieAsync(Guid mediaItemId, bool watched, CancellationToken ct = default);
    Task MarkEpisodeAsync(Guid mediaItemId, int season, int episode, bool watched, string? filePath = null, CancellationToken ct = default);
    Task MarkAllAsync(Guid mediaItemId, bool watched, int totalEpisodes, CancellationToken ct = default);

    /// <summary>Record a progress tick from the player. <paramref name="positionMs"/>
    /// is the current seek position; <paramref name="runtimeMs"/> is the file's
    /// total runtime (for the 0..1 progress fraction); <paramref name="playedDeltaSec"/>
    /// is seconds played since the last tick (for cumulative totals).
    /// Auto-flips IsWatched=true once position ≥ 90% of runtime.</summary>
    Task RecordProgressAsync(
        Guid mediaItemId, int? season, int? episode, string? filePath,
        long positionMs, long? runtimeMs, int playedDeltaSec,
        CancellationToken ct = default);

    Task ClearAsync(Guid mediaItemId, CancellationToken ct = default);

    Task<TimeSpan> GetGlobalWatchTimeAsync(CancellationToken ct = default);

    /// <summary>Items the user is in the middle of (any episode/movie with
    /// progress > 0% but &lt; 90%). Ordered by LastSeenAt desc.</summary>
    Task<IReadOnlyList<WatchState>> GetContinueWatchingAsync(int limit = 12, CancellationToken ct = default);

    event Action<Guid>? WatchStateChanged;
}

public class WatchStateService(IDbContextFactory<AppDbContext> dbFactory, ILogger<WatchStateService> logger)
    : IWatchStateService
{
    /// <summary>Auto-mark-watched threshold per CHANGELOG §1 ("auto-set when progress hits 100%").
    /// 0.9 is the de-facto industry standard — closer to 1.0 punishes users who exit during credits.</summary>
    private const double AutoWatchedThreshold = 0.9;

    public event Action<Guid>? WatchStateChanged;

    private void Notify(Guid mediaItemId)
    {
        try { WatchStateChanged?.Invoke(mediaItemId); }
        catch (Exception ex) { logger.LogWarning(ex, "WatchStateChanged handler threw"); }
    }

    public async Task<WatchState?> GetForMovieAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WatchStates.FirstOrDefaultAsync(
            w => w.MediaItemId == mediaItemId && w.Season == null && w.Episode == null, ct);
    }

    public async Task<WatchState?> GetForEpisodeAsync(Guid mediaItemId, int season, int episode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WatchStates.FirstOrDefaultAsync(
            w => w.MediaItemId == mediaItemId && w.Season == season && w.Episode == episode, ct);
    }

    public async Task<IReadOnlyList<WatchState>> GetForSeriesAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WatchStates
            .Where(w => w.MediaItemId == mediaItemId)
            .OrderBy(w => w.Season).ThenBy(w => w.Episode)
            .ToListAsync(ct);
    }

    public async Task<int> CountWatchedEpisodesAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WatchStates.CountAsync(
            w => w.MediaItemId == mediaItemId && w.IsWatched && w.Episode != null, ct);
    }

    public async Task MarkMovieAsync(Guid mediaItemId, bool watched, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WatchStates.FirstOrDefaultAsync(
            w => w.MediaItemId == mediaItemId && w.Season == null && w.Episode == null, ct);
        if (row is null)
        {
            row = new WatchState
            {
                Id          = Guid.NewGuid(),
                MediaItemId = mediaItemId,
                Season      = null,
                Episode     = null,
                CreatedAt   = DateTime.UtcNow,
            };
            db.WatchStates.Add(row);
        }
        row.IsWatched   = watched;
        row.LastSeenAt  = DateTime.UtcNow;
        // Marking as watched without playing also pins progress to "end" so the
        // UI's progress-bar matches the chip.
        if (watched && row.RuntimeMs is { } rt) row.ProgressMs = rt;
        await db.SaveChangesAsync(ct);
        Notify(mediaItemId);
    }

    public async Task MarkEpisodeAsync(Guid mediaItemId, int season, int episode, bool watched, string? filePath = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WatchStates.FirstOrDefaultAsync(
            w => w.MediaItemId == mediaItemId && w.Season == season && w.Episode == episode, ct);
        if (row is null)
        {
            row = new WatchState
            {
                Id          = Guid.NewGuid(),
                MediaItemId = mediaItemId,
                Season      = season,
                Episode     = episode,
                FilePath    = filePath,
                CreatedAt   = DateTime.UtcNow,
            };
            db.WatchStates.Add(row);
        }
        row.IsWatched   = watched;
        row.LastSeenAt  = DateTime.UtcNow;
        if (watched && row.RuntimeMs is { } rt) row.ProgressMs = rt;
        if (filePath is not null && row.FilePath is null) row.FilePath = filePath;
        await db.SaveChangesAsync(ct);
        Notify(mediaItemId);
    }

    public async Task MarkAllAsync(Guid mediaItemId, bool watched, int totalEpisodes, CancellationToken ct = default)
    {
        // For series: explicitly create / update rows for episodes 1..total of season 1.
        // We don't know the full season decomposition here without parsing SeasonsJson,
        // so this method's primary use is the "Mark all watched" Manage button on
        // a single-season series. Multi-season series should call MarkEpisodeAsync
        // per (season, episode) — drivers from the UI side.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WatchStates
            .Where(w => w.MediaItemId == mediaItemId && w.Episode != null)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(w => (w.Season ?? 1, w.Episode!.Value));
        for (int e = 1; e <= totalEpisodes; e++)
        {
            if (!byKey.TryGetValue((1, e), out var row))
            {
                row = new WatchState
                {
                    Id          = Guid.NewGuid(),
                    MediaItemId = mediaItemId,
                    Season      = 1,
                    Episode     = e,
                    CreatedAt   = DateTime.UtcNow,
                };
                db.WatchStates.Add(row);
            }
            row.IsWatched  = watched;
            row.LastSeenAt = DateTime.UtcNow;
            if (watched && row.RuntimeMs is { } rt) row.ProgressMs = rt;
        }
        await db.SaveChangesAsync(ct);
        Notify(mediaItemId);
    }

    public async Task RecordProgressAsync(
        Guid mediaItemId, int? season, int? episode, string? filePath,
        long positionMs, long? runtimeMs, int playedDeltaSec,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WatchStates.FirstOrDefaultAsync(
            w => w.MediaItemId == mediaItemId && w.Season == season && w.Episode == episode, ct);
        if (row is null)
        {
            row = new WatchState
            {
                Id          = Guid.NewGuid(),
                MediaItemId = mediaItemId,
                Season      = season,
                Episode     = episode,
                FilePath    = filePath,
                PlayCount   = 1,
                CreatedAt   = DateTime.UtcNow,
            };
            db.WatchStates.Add(row);
        }
        var now = DateTime.UtcNow;
        row.ProgressMs        = positionMs;
        if (runtimeMs is { } rt) row.RuntimeMs = rt;
        row.TotalWatchTimeSec += Math.Max(0, playedDeltaSec);
        row.LastSeenAt        = now;
        if (filePath is not null && row.FilePath is null) row.FilePath = filePath;
        // Journal the played delta for the stats surface. UserId=null mirrors
        // this service's anonymous WatchState rows (external mpv tracker).
        await WatchEventRecorder.RecordAsync(
            db, userId: null, mediaItemId, season, episode, Math.Max(0, playedDeltaSec), now, ct);

        // Auto-flip watched when the player crosses the threshold.
        if (!row.IsWatched && runtimeMs is > 0 && positionMs >= runtimeMs * AutoWatchedThreshold)
            row.IsWatched = true;

        await db.SaveChangesAsync(ct);
        Notify(mediaItemId);
    }

    public async Task ClearAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.WatchStates
            .Where(w => w.MediaItemId == mediaItemId)
            .ExecuteDeleteAsync(ct);
        Notify(mediaItemId);
    }

    public async Task<TimeSpan> GetGlobalWatchTimeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var totalSec = await db.WatchStates.SumAsync(w => (long?)w.TotalWatchTimeSec, ct) ?? 0;
        return TimeSpan.FromSeconds(totalSec);
    }

    public async Task<IReadOnlyList<WatchState>> GetContinueWatchingAsync(int limit = 12, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // "In progress" = has a position, hasn't been auto-marked watched, last touched recently.
        return await db.WatchStates
            .Where(w => !w.IsWatched && w.ProgressMs != null && w.ProgressMs > 0)
            .OrderByDescending(w => w.LastSeenAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
