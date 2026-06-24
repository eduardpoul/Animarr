using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Runs the detection cascade and persists results to <see cref="EpisodeSegment"/>.
/// Providers run cheapest-first (by <see cref="ISegmentProvider.Order"/>); for an
/// episode we stop as soon as both an intro and a credits boundary are in hand.
///
/// Writes respect source precedence: a lower-ranked <see cref="SegmentSource"/>
/// never overwrites a better one, and <see cref="SegmentSource.Manual"/> rows are
/// never touched. The unique (item, season, episode, kind) index makes every
/// write an idempotent upsert, so re-running is safe and self-skipping.
/// </summary>
public sealed class SegmentDetectionService(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaFileResolver resolver,
    IEnumerable<ISegmentProvider> providers,
    ILogger<SegmentDetectionService> logger)
{
    private readonly ISegmentProvider[] _providers = providers.OrderBy(p => p.Order).ToArray();

    /// <summary>Detect segments for every on-disk episode of an item that doesn't
    /// already have both an intro and credits. <paramref name="cheapOnly"/> limits
    /// to network/metadata providers (AniSkip, chapters) — pass false from the
    /// background pass to also run media-decoding providers. Returns the number of
    /// episodes for which something was written.</summary>
    public async Task<int> DetectForItemAsync(Guid mediaItemId, bool cheapOnly, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return 0;

        MediaFileDto[] files;
        try { files = await resolver.ResolveAsync(mediaItemId, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "[Segments] file resolve failed for {Id}", mediaItemId); return 0; }

        var episodes = files
            .Where(f => f.Episode is not null && !string.IsNullOrEmpty(f.FilePath))
            .GroupBy(f => (Season: f.Season ?? 1, Episode: f.Episode!.Value))
            .Select(g => (g.Key.Season, g.Key.Episode, g.First().FilePath))
            .ToList();
        if (episodes.Count == 0) return 0;

        // Episodes that already have BOTH intro and credits — skip the work.
        var covered = (await db.EpisodeSegments
                .Where(s => s.MediaItemId == mediaItemId &&
                            (s.Kind == SegmentKind.Intro || s.Kind == SegmentKind.Credits))
                .Select(s => new { s.Season, s.Episode, s.Kind })
                .ToListAsync(ct))
            .GroupBy(s => (s.Season, s.Episode))
            .Where(g => g.Any(x => x.Kind == SegmentKind.Intro) && g.Any(x => x.Kind == SegmentKind.Credits))
            .Select(g => g.Key)
            .ToHashSet();

        int touched = 0;
        foreach (var (season, episode, filePath) in episodes)
        {
            ct.ThrowIfCancellationRequested();
            if (covered.Contains((season, episode))) continue;
            try
            {
                if (await DetectForEpisodeAsync(item, season, episode, filePath, cheapOnly, ct))
                    touched++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "[Segments] episode detect failed for {File}", filePath); }
        }

        if (touched > 0)
            logger.LogInformation("[Segments] wrote segments for {Count} episode(s) of {Title}", touched, item.Title);
        return touched;
    }

    /// <summary>Run the cascade for a single episode and upsert what it finds.
    /// Returns true when at least one row was written/updated. Used both by the
    /// batch pass and the player's lazy per-episode lookup.</summary>
    public async Task<bool> DetectForEpisodeAsync(
        MediaItem item, int season, int episode, string filePath, bool cheapOnly, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

        var dur = await MediaProbe.GetDurationAsync(filePath, ct);
        var ctx = new SegmentEpisodeContext(item, season, episode, filePath, dur);

        // First provider (highest cascade priority) to supply a kind wins this run.
        var detected = new Dictionary<SegmentKind, (DetectedSegment Seg, SegmentSource Source)>();
        foreach (var provider in _providers)
        {
            if (cheapOnly && !provider.Cheap) continue;
            if (detected.ContainsKey(SegmentKind.Intro) && detected.ContainsKey(SegmentKind.Credits)) break;
            if (!provider.CanRun(ctx)) continue;

            IReadOnlyList<DetectedSegment> raw;
            try { raw = await provider.DetectAsync(ctx, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Segments] {Provider} failed for {File}", provider.Source, filePath);
                continue;
            }

            foreach (var seg in SegmentSanity.Clean(ctx, raw))
                if (!detected.ContainsKey(seg.Kind))
                    detected[seg.Kind] = (seg, provider.Source);
        }

        if (detected.Count == 0) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.EpisodeSegments
            .Where(s => s.MediaItemId == item.Id && s.Season == season && s.Episode == episode)
            .ToListAsync(ct);

        bool wrote = false;
        foreach (var (kind, (seg, source)) in detected)
        {
            var row = rows.FirstOrDefault(s => s.Kind == kind);
            if (row is null)
            {
                db.EpisodeSegments.Add(new EpisodeSegment
                {
                    MediaItemId   = item.Id,
                    Season        = season,
                    Episode       = episode,
                    Kind          = kind,
                    StartSec      = seg.StartSec,
                    EndSec        = seg.EndSec,
                    Source        = source,
                    DetectedAtUtc = DateTime.UtcNow,
                });
                wrote = true;
            }
            // Precedence: equal-or-higher rank may refresh; Manual (100) outranks
            // every detector, so an auto source can never clobber it.
            else if ((int)source >= (int)row.Source)
            {
                row.StartSec      = seg.StartSec;
                row.EndSec        = seg.EndSec;
                row.Source        = source;
                row.DetectedAtUtc = DateTime.UtcNow;
                wrote = true;
            }
        }

        if (wrote) await db.SaveChangesAsync(ct);
        return wrote;
    }
}
