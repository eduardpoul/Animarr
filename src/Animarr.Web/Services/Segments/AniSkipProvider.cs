using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Cascade level 0: AniSkip crowd-sourced timestamps. Cheapest and usually the
/// most accurate source for anime — a single network call keyed by MAL id, no
/// file decoding, no CPU.
///
/// Numbering caveat: AniSkip keys on (malId, episodeNumber) where MAL normally
/// gives each anime season its OWN id numbered from 1. Animarr stores one
/// <see cref="MediaItem.MalId"/> per title, so the mapping is only unambiguous
/// for a single-season title or Season 1 of a multi-season one. For Season ≥ 2
/// we'd need a per-season MAL id we don't have — so we skip and let a later
/// cascade level (chromaprint) or the player's 95% fallback handle it. A wrong
/// timestamp is worse than none.
/// </summary>
public sealed class AniSkipProvider(AniSkipClient client, ILogger<AniSkipProvider> logger) : ISegmentProvider
{
    public SegmentSource Source => SegmentSource.AniSkip;
    public int Order => 0;
    public bool Cheap => true;   // a single network call, safe to run on demand

    public bool CanRun(SegmentEpisodeContext ctx)
        => ctx.Item.MalId is > 0 && TryResolveEpisodeNumber(ctx, out _);

    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(SegmentEpisodeContext ctx, CancellationToken ct)
    {
        if (ctx.Item.MalId is not int malId || malId <= 0) return Array.Empty<DetectedSegment>();
        if (!TryResolveEpisodeNumber(ctx, out var episodeNumber))
        {
            logger.LogDebug("[AniSkip] skipped {Title} S{S}E{E} — ambiguous MAL episode number",
                ctx.Item.Title, ctx.Season, ctx.Episode);
            return Array.Empty<DetectedSegment>();
        }

        var intervals = await client.GetSkipTimesAsync(malId, episodeNumber, ctx.DurationSec, ct);
        if (intervals.Count == 0) return Array.Empty<DetectedSegment>();

        var result = new List<DetectedSegment>(intervals.Count);
        foreach (var iv in intervals)
        {
            SegmentKind? kind = iv.SkipType switch
            {
                "op" or "mixed-op" => SegmentKind.Intro,
                "ed" or "mixed-ed" => SegmentKind.Credits,
                "recap"            => SegmentKind.Recap,
                _                  => null,
            };
            if (kind is null) continue;
            result.Add(new DetectedSegment(kind.Value, iv.StartTime, iv.EndTime));
        }

        logger.LogInformation("[AniSkip] mal={Mal} ep={Ep} → {Count} segment(s)", malId, episodeNumber, result.Count);
        return result;
    }

    /// <summary>Maps Animarr's disk (season, episode) onto the episode number
    /// AniSkip expects for this title's MAL id. Conservative: only Season ≤ 1
    /// (single-season title, or S1 of a multi-season one) is unambiguous; returns
    /// false otherwise so the caller skips AniSkip rather than risk a wrong key.</summary>
    private static bool TryResolveEpisodeNumber(SegmentEpisodeContext ctx, out int episodeNumber)
    {
        episodeNumber = 0;
        if (ctx.Episode <= 0) return false;
        if (ctx.Season > 1) return false;   // needs a per-season MAL id we don't store
        episodeNumber = ctx.Episode;
        return true;
    }
}
