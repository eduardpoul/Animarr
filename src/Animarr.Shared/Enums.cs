namespace Animarr.Shared;

// ─── Folder / library shape ───────────────────────────────────────────────

/// <summary>
/// What kind of content lives in a folder. <c>Auto</c> means the indexer
/// guesses based on filename patterns; <c>Series</c> / <c>Movie</c> pin the
/// heuristic so misclassification can be corrected by the user.
/// </summary>
public enum FolderType
{
    Auto   = 0,
    Series = 1,
    Movie  = 2,
}

/// <summary>
/// Coarse classification used by the catalog grid + identification scoring.
/// <c>Unknown</c> means we haven't run identification yet (or it produced
/// no usable answer).
/// </summary>
public enum MediaItemType
{
    Unknown     = 0,
    Anime       = 1,
    Series      = 2,
    Movie       = 3,
    Multserials = 4,
}

/// <summary>
/// Lifecycle of a single media-item's metadata. Drives the "Needs review"
/// chip in the sidebar and the candidate-picker dialog.
/// </summary>
public enum IdentificationStatus
{
    Pending      = 0,
    Identified   = 1,
    NeedsReview  = 2,
    Failed       = 3,
    Manual       = 4,
}

// ─── Torrents ─────────────────────────────────────────────────────────────

/// <summary>
/// High-level torrent state used by the UI list. Mirrored from
/// MonoTorrent's internal state but flattened to what users care about.
/// </summary>
public enum TorrentRecordState
{
    Downloading = 0,
    Seeding     = 1,
    Paused      = 2,
    Stopped     = 3,
    Error       = 4,
    Metadata    = 5,  // magnet waiting for metadata
    Hashing     = 6,
}

// ─── Rename queue / history ───────────────────────────────────────────────

public enum RenameQueueStatus
{
    Queued     = 0,
    Processing = 1,
    Done       = 2,
    Error      = 3,
}

public enum RenameQueueSource
{
    Watcher      = 0,
    AutoDownload = 1,
}

public enum RenameStatus
{
    Pending  = 0,
    Renamed  = 1,
    Skipped  = 2,
    Error    = 3,
    Reverted = 4,
}

// ─── Identification queue ─────────────────────────────────────────────────

public enum IdentificationQueueStatus
{
    Queued     = 0,
    Processing = 1,
    Done       = 2,
    Failed     = 3,
}

// ─── Patterns / ignore rules ──────────────────────────────────────────────

public enum PatternScope
{
    Global         = 0,
    FolderOverride = 1,
}

public enum RuleScope
{
    Global         = 0,
    FolderOverride = 1,
}
