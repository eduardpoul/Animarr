namespace Animarr.Web.Data.Models;

/// <summary>Kind of timeline segment detected for an episode.</summary>
public enum SegmentKind
{
    /// <summary>Opening theme / title sequence near the start.</summary>
    Intro   = 0,
    /// <summary>End credits / ending theme near the finish. Drives the
    /// "next episode" suggestion (the player surfaces Next-Up at this start).</summary>
    Credits = 1,
    /// <summary>"Previously on…" recap before the intro.</summary>
    Recap   = 2,
}

/// <summary>
/// Where a segment came from. The numeric value doubles as a precedence rank:
/// a higher rank is more trustworthy and a lower-ranked source never overwrites
/// it (see <see cref="EpisodeSegment"/>). Manual edits outrank everything.
/// </summary>
public enum SegmentSource
{
    /// <summary>Black-frame / silence video analysis — used only to refine an
    /// ending boundary, so it sits at the bottom.</summary>
    BlackFrame  = 10,
    /// <summary>Audio fingerprint (Chromaprint) compared pairwise across a season.</summary>
    Chromaprint = 20,
    /// <summary>Embedded container chapters with recognisable names.</summary>
    Chapter     = 30,
    /// <summary>AniSkip crowd-sourced timestamps (keyed by MAL id + episode).</summary>
    AniSkip     = 40,
    /// <summary>A user's manual correction — never clobbered by detection.</summary>
    Manual      = 100,
}

/// <summary>
/// A detected (or hand-edited) timeline segment for a single episode — the
/// opening to skip, the end-credits boundary that triggers "next episode", or a
/// recap. One row per (item, season, episode, kind); the unique index makes the
/// detection pass an idempotent upsert.
///
/// Precedence mirrors <see cref="EpisodeFileMapping"/>: a detection pass writes
/// a row only when its <see cref="Source"/> rank is at least the stored row's
/// rank, so <see cref="SegmentSource.Manual"/> survives re-scans and a cheap
/// source never downgrades a better one. Times are seconds from the start of the
/// file (matching the player's currentTime), as doubles for sub-second precision.
/// </summary>
public class EpisodeSegment
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Disk season (matches <c>MediaFileDto.Season</c>; 1 when the
    /// resolver defaults a single-season series).</summary>
    public int Season { get; set; }

    /// <summary>Disk episode number (matches <c>MediaFileDto.Episode</c>).</summary>
    public int Episode { get; set; }

    public SegmentKind Kind { get; set; }

    /// <summary>Segment start, seconds from the start of the file.</summary>
    public double StartSec { get; set; }

    /// <summary>Segment end, seconds from the start of the file.</summary>
    public double EndSec { get; set; }

    public SegmentSource Source { get; set; }

    public DateTime DetectedAtUtc { get; set; }
}
