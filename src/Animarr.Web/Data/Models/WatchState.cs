namespace Animarr.Web.Data.Models;

/// <summary>
/// Per-file watch progress / completion state. Lazy-created — a row only
/// exists once the user touches the item (marks watched, or a player reports
/// progress). Movies use a single row with Season/Episode = null; series get
/// one row per (season, episode) pair the user has interacted with.
///
/// Spec: design_handoff_animarr/CHANGELOG_FOR_CLAUDE_CODE.md §1.
/// </summary>
public class WatchState
{
    public Guid Id { get; set; }

    /// <summary>FK → MediaItem.Id. Cascade-deleted with the item.</summary>
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Season number for series episodes; NULL for movies and one-file flat entries.</summary>
    public int? Season { get; set; }

    /// <summary>Episode number within the season; NULL for movies.</summary>
    public int? Episode { get; set; }

    /// <summary>Absolute path of the file the state applies to. Helps disambiguate
    /// when the same MediaItem has multiple files (alt versions) AND survives a
    /// metadata refresh that doesn't move the file. Optional.</summary>
    public string? FilePath { get; set; }

    /// <summary>Explicit watched flag. Set manually by the user OR auto-flipped
    /// when ProgressMs ≥ 90% of total runtime by RecordProgressAsync.</summary>
    public bool IsWatched { get; set; }

    /// <summary>Resume position in milliseconds. NULL = never started; 0 = explicitly
    /// rewound to start; positive = mid-playback. CHANGELOG names this `progressMs`.</summary>
    public long? ProgressMs { get; set; }

    /// <summary>Total runtime in milliseconds (snapshot from the player at last touch).
    /// Used to compute the 0..1 progress fraction in the UI.</summary>
    public long? RuntimeMs { get; set; }

    /// <summary>Cumulative seconds the file has been actively played across all sessions.
    /// Drives "you spent N hours on this series" stats. Updates additively in
    /// RecordProgressAsync; does NOT reset when the user rewinds.</summary>
    public long TotalWatchTimeSec { get; set; }

    /// <summary>Number of separate playback sessions. Increments on each
    /// "Play" trigger (or first progress ping after a quiet period).</summary>
    public int PlayCount { get; set; }

    /// <summary>Most recent activity timestamp — CHANGELOG names this `lastSeenAt`.
    /// Drives the "Continue watching" rail ordering and recency badges.</summary>
    public DateTime? LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
