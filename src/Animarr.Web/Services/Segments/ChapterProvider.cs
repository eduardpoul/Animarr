using System.Text.RegularExpressions;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Cascade level 1: embedded container chapters. Reads chapter markers via
/// ffprobe and maps ones whose title looks like an opening/ending/recap onto the
/// matching segment kind. Cheap (metadata only, no decoding) but low-yield for
/// anime — most rips have no chapters or unnamed ones. Runs after AniSkip and
/// before the chromaprint analysis.
/// </summary>
public sealed partial class ChapterProvider(ILogger<ChapterProvider> logger) : ISegmentProvider
{
    public SegmentSource Source => SegmentSource.Chapter;
    public int Order => 10;
    public bool Cheap => true;   // ffprobe metadata read, no media decoding

    public bool CanRun(SegmentEpisodeContext ctx) => !string.IsNullOrEmpty(ctx.FilePath);

    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(SegmentEpisodeContext ctx, CancellationToken ct)
    {
        var chapters = await MediaProbe.GetChaptersAsync(ctx.FilePath, ct);
        if (chapters.Count == 0) return Array.Empty<DetectedSegment>();

        var result = new List<DetectedSegment>();
        foreach (var ch in chapters)
        {
            if (string.IsNullOrWhiteSpace(ch.Title) || ch.EndSec <= ch.StartSec) continue;
            var title = ch.Title.Trim();
            if (IntroRegex().IsMatch(title))
                result.Add(new DetectedSegment(SegmentKind.Intro, ch.StartSec, ch.EndSec));
            else if (CreditsRegex().IsMatch(title))
                result.Add(new DetectedSegment(SegmentKind.Credits, ch.StartSec, ch.EndSec));
            else if (RecapRegex().IsMatch(title))
                result.Add(new DetectedSegment(SegmentKind.Recap, ch.StartSec, ch.EndSec));
        }

        if (result.Count > 0)
            logger.LogInformation("[Chapter] {File} → {Count} named segment(s)",
                Path.GetFileName(ctx.FilePath), result.Count);
        return result;
    }

    // Word-boundary matches so an "Episode 1" title can't trip the bare "op"/"ed".
    [GeneratedRegex(@"\b(opening|intro|op|avant)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntroRegex();
    [GeneratedRegex(@"\b(ending|credits|outro|ed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsRegex();
    [GeneratedRegex(@"\b(recap|previously)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecapRegex();
}
