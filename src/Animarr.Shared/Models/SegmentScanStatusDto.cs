namespace Animarr.Shared.Models;

/// <summary>Progress of the skip-intro/credits background analysis, for the
/// Settings indicator. <see cref="ScannedTitles"/> is how many identified titles
/// the background pass has finished; <see cref="TitlesWithSegments"/> is how many
/// actually got at least one segment.</summary>
public sealed record SegmentScanStatusDto(
    int TotalTitles,
    int ScannedTitles,
    int TitlesWithSegments);
