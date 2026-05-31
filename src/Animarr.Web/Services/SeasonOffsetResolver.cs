using System.Globalization;
using System.Text.Json;
using Animarr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Animarr.Web.Services;

/// <summary>
/// Donghua/anime season handling: when TMDB lists the whole run as ONE absolute
/// season but the disk is split into seasons, compute a per-season "absolute
/// offset" so disk (Season N, Episode E) can point at the right TMDB episode
/// (e.g. disk S3E1 → TMDB absolute episode 107).
///
/// Keyless + self-validating — uses ONLY TMDB, which is already configured:
///   1. Pull the single season's episode list (with air_date).
///   2. Cut it into blocks at large air-date gaps — a season boundary is a
///      months-long pause between two otherwise weekly episodes.
///   3. Map disk Season N to the N-th aired block; offset = block.Start - 1.
///   4. Validate the block size against the disk season's file count.
/// Result is stored on MediaItem.SeasonOffsetsJson; null when not applicable
/// (TMDB already multi-season, or a single-season disk).
/// </summary>
public sealed class SeasonOffsetResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaFileResolver resolver,
    TmdbClient tmdb,
    ILogger<SeasonOffsetResolver> logger)
{
    /// <summary>A gap larger than this between consecutive episodes' air dates
    /// marks a season boundary. Donghua seasons are ~a year apart while in-season
    /// episodes are weekly, so 90 days separates cleanly without catching the odd
    /// mid-season break.</summary>
    private const int SeasonGapDays = 90;

    /// <summary>Matches MetadataService's SeasonsJson serialization (default
    /// PascalCase casing) so the rewritten season list deserializes identically.</summary>
    private static readonly JsonSerializerOptions SeasonJson = new() { WriteIndented = false };

    /// <summary>Compute &amp; store per-season offsets. Returns the map written
    /// (diskSeason→offset), or null when nothing applies.</summary>
    public async Task<Dictionary<int, int>?> ResolveAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null || item.TmdbId is not int tmdbId || tmdbId <= 0) return null;

        // Act ONLY on disks that are SPLIT into seasons (a "Season N>1" folder).
        // A normal single-season show — even a split-cour one TMDB keeps as one
        // season — has no season>1 on disk, so we leave it untouched; this also
        // keeps us out of TMDB for the common case. The guard reads disk + a
        // FRESH TMDB fetch only (never the stored SeasonsJson, which this method
        // overwrites), so re-derivation is idempotent.
        var files = await resolver.ResolveAsync(mediaItemId, ct);
        var diskSeasons = files
            .Where(f => f.Season is > 1 && f.Episode is > 0)
            .GroupBy(f => f.Season!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        if (diskSeasons.Count == 0) return null;

        // TMDB season-1 episode list — ONLY aired episodes (real air_date). TMDB
        // pads ongoing runs with future/placeholder entries (null air_date) that
        // have no date-gap and would otherwise merge into the last block and
        // overstate the final season (A Will Eternal: TMDB lists 200, but only
        // 165 have aired → real seasons 52/54/59). Range-based block counts
        // (End-Start+1) tolerate the odd missing date in the middle.
        var detail = await tmdb.GetSeasonDetailAsync(tmdbId, 1, ct);
        var eps = detail?.Episodes?
            .Where(e => e.EpisodeNumber > 0 && ParseDate(e.AirDate) is not null)
            .OrderBy(e => e.EpisodeNumber)
            .ToList();
        if (eps is null || eps.Count == 0) return null;

        var blocks = PartitionByAirDateGaps(eps);
        // <2 blocks → TMDB isn't an absolute multi-season run (normal single
        // season, or already split) → nothing to re-split.
        if (blocks.Count < 2) return null;

        // Re-split the single absolute TMDB season into the real seasons the
        // air-date gaps reveal, so the UI shows Season 1/2/3 (e.g. 52/54/59)
        // instead of "Season 1 = <whole run>" + an orphan disk season.
        var derived = new List<SeasonMeta>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            int n = i + 1;
            int count = diskSeasons.TryGetValue(n, out var dc) ? Math.Max(b.Count, dc) : b.Count;
            derived.Add(new SeasonMeta
            {
                Number       = n,
                EpisodeCount = count,
                Name         = $"Season {n}",
                AirDate      = eps.FirstOrDefault(e => e.EpisodeNumber == b.Start)?.AirDate,
            });
        }
        item.SeasonsJson  = JsonSerializer.Serialize(derived, SeasonJson);
        item.EpisodeCount = derived.Sum(s => s.EpisodeCount);
        logger.LogInformation("SeasonOffset {Id}: re-split TMDB absolute season into {N} seasons ({Counts}).",
            mediaItemId, derived.Count, string.Join("/", derived.Select(s => s.EpisodeCount)));

        // Per-season absolute offsets for the disk's seasons (>1). Season 1 needs
        // none (absolute == episode there). disk Season N → block N (aired order);
        // offset = block.Start - 1 ⇒ AbsoluteEpisode = offset + episode.
        var offsets = new Dictionary<int, int>();
        foreach (var (season, diskCount) in diskSeasons.OrderBy(k => k.Key))
        {
            int idx = season - 1;
            if (idx < 0 || idx >= blocks.Count) continue;
            var blk = blocks[idx];
            bool isLast = idx == blocks.Count - 1;
            // Middle blocks are complete → the disk season must fit inside. The
            // last block may lag behind disk (ongoing season TMDB hasn't fully
            // catalogued), so trust its start regardless of size.
            if (diskCount <= blk.Count || isLast)
                offsets[season] = blk.Start - 1;
        }
        item.SeasonOffsetsJson = offsets.Count > 0
            ? JsonSerializer.Serialize(offsets.ToDictionary(k => k.Key.ToString(), v => v.Value))
            : null;

        await db.SaveChangesAsync(ct);
        return offsets.Count > 0 ? offsets : null;
    }

    /// <summary>Split the ordered episode list into contiguous blocks wherever
    /// two consecutive episodes are more than <see cref="SeasonGapDays"/> apart.</summary>
    private static List<(int Start, int End, int Count)> PartitionByAirDateGaps(
        List<TmdbEpisode> eps)
    {
        var blocks = new List<(int Start, int End, int Count)>();
        int start = eps[0].EpisodeNumber;
        for (int i = 1; i < eps.Count; i++)
        {
            var prev = ParseDate(eps[i - 1].AirDate);
            var cur = ParseDate(eps[i].AirDate);
            if (prev is not null && cur is not null && (cur.Value - prev.Value).TotalDays > SeasonGapDays)
            {
                blocks.Add((start, eps[i - 1].EpisodeNumber, eps[i - 1].EpisodeNumber - start + 1));
                start = eps[i].EpisodeNumber;
            }
        }
        blocks.Add((start, eps[^1].EpisodeNumber, eps[^1].EpisodeNumber - start + 1));
        return blocks;
    }

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
