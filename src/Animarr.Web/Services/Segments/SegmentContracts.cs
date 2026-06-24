using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>One episode handed to the detection cascade. <see cref="DurationSec"/>
/// is the file runtime in seconds (0 when unknown); providers use it for the
/// AniSkip episodeLength hint, chromaprint tail window, and sanity checks.</summary>
public sealed record SegmentEpisodeContext(
    MediaItem Item,
    int Season,
    int Episode,
    string FilePath,
    double DurationSec);

/// <summary>A detected segment before persistence. Times are seconds from the
/// start of the file (matching the player's currentTime).</summary>
public sealed record DetectedSegment(SegmentKind Kind, double StartSec, double EndSec);

/// <summary>
/// One strategy in the detection cascade (AniSkip, chapters, chromaprint, …).
/// The orchestrator runs providers ordered by <see cref="Order"/> cheapest-first
/// and stops once it has both an intro and a credits boundary.
/// </summary>
public interface ISegmentProvider
{
    /// <summary>Stamp written to <see cref="EpisodeSegment.Source"/>; its numeric
    /// value doubles as the precedence rank.</summary>
    SegmentSource Source { get; }

    /// <summary>Cascade order — lower runs first.</summary>
    int Order { get; }

    /// <summary>True when this provider is fast enough to run synchronously on a
    /// player request (network/metadata only — AniSkip, chapters). False for
    /// providers that decode media (chromaprint, black-frame), which only run in
    /// the background pass.</summary>
    bool Cheap { get; }

    /// <summary>Cheap pre-check (e.g. AniSkip needs a MAL id). Lets the
    /// orchestrator skip the provider without spawning work.</summary>
    bool CanRun(SegmentEpisodeContext ctx);

    Task<IReadOnlyList<DetectedSegment>> DetectAsync(SegmentEpisodeContext ctx, CancellationToken ct);
}

/// <summary>
/// Drops obviously-bad detected segments and clamps the rest inside the file.
/// Keeps the pipeline robust against junk (bad crowd-sourced rows, a chapter
/// named "Intro" that spans the whole episode, a chromaprint false match). The
/// position checks only apply when the duration is known.
/// </summary>
public static class SegmentSanity
{
    public static List<DetectedSegment> Clean(SegmentEpisodeContext ctx, IEnumerable<DetectedSegment> segs)
    {
        var dur = ctx.DurationSec;
        var ok  = new List<DetectedSegment>();
        foreach (var s in segs)
        {
            double start = s.StartSec, end = s.EndSec;
            if (double.IsNaN(start) || double.IsNaN(end)) continue;
            if (start < 0) start = 0;
            if (end <= start) continue;
            if (dur > 0)
            {
                if (start >= dur) continue;
                if (end > dur) end = dur;
            }

            double len = end - start;
            if (len < 3) continue;   // too short to be a real segment

            // An intro/recap longer than ~4 minutes is almost certainly a bad match.
            if (s.Kind is SegmentKind.Intro or SegmentKind.Recap && len > 240) continue;

            if (dur > 0)
            {
                // Intro/recap belong early; credits belong late. Generous cutoffs
                // so unusual-but-real layouts still pass.
                if (s.Kind is SegmentKind.Intro or SegmentKind.Recap && start > Math.Max(600, dur * 0.5)) continue;
                if (s.Kind is SegmentKind.Credits && end < dur * 0.5) continue;
            }

            ok.Add(s with { StartSec = start, EndSec = end });
        }
        return ok;
    }
}
