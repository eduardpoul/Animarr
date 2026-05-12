namespace Animarr.Web.Data.Models;

public enum MediaItemType
{
    Unknown = 0,
    Anime   = 1,
    Series  = 2,
    Movie   = 3,
}

public enum IdentificationStatus
{
    Pending      = 0,
    Identified   = 1,
    NeedsReview  = 2,
    Failed       = 3,
    Manual       = 4,
}

/// <summary>
/// Stores metadata for a media folder (series, anime, movie).
/// One MediaItem per FolderWatcher (IsSection=false only).
/// Image paths are relative to the folder root (e.g. "poster.jpg").
/// </summary>
public class MediaItem
{
    public Guid Id { get; set; }

    public Guid FolderId { get; set; }
    public FolderWatcher Folder { get; set; } = null!;

    // ─── Identity ──────────────────────────────────────────────────────────
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public MediaItemType MediaType { get; set; } = MediaItemType.Unknown;

    // ─── External IDs ──────────────────────────────────────────────────────
    public int?    TmdbId { get; set; }
    public int?    MalId  { get; set; }
    public string? ImdbId { get; set; }
    public int?    TvdbId { get; set; }

    // ─── Local image paths (relative to FolderWatcher.Path) ───────────────
    /// <summary>poster.jpg — primary poster art (portrait)</summary>
    public string? PosterPath { get; set; }
    /// <summary>fanart.jpg — wide backdrop used as hero/background</summary>
    public string? FanartPath { get; set; }
    /// <summary>logo.png — transparent title logo (if available from TMDB)</summary>
    public string? LogoPath { get; set; }

    // ─── Metadata ──────────────────────────────────────────────────────────
    public string? Description { get; set; }
    public string? Tagline { get; set; }
    /// <summary>JSON array of genre names: ["Action","Drama"]</summary>
    public string? GenresJson { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }
    /// <summary>e.g. "Ended", "Returning Series", "Canceled", "Finished Airing"</summary>
    public string? Status { get; set; }
    /// <summary>Age rating: PG-13, R, TV-MA, PG, etc.</summary>
    public string? ContentRating { get; set; }
    /// <summary>Runtime in minutes</summary>
    public int? Runtime { get; set; }
    /// <summary>
    /// JSON array of SeasonMeta objects:
    /// [{"number":1,"episodeCount":13,"posterPath":"season01/poster.jpg","overview":"...","airDate":"2013-04-07"}]
    /// </summary>
    public string? SeasonsJson { get; set; }
    /// <summary>
    /// JSON array of top-3 search candidates when status is NeedsReview.
    /// Cleared after user picks one.
    /// </summary>
    public string? CandidatesJson { get; set; }

    // ─── LLM ───────────────────────────────────────────────────────────────
    /// <summary>Title extracted by LLM before external search (for debugging/display)</summary>
    public string? LlmIdentifiedTitle { get; set; }
    public double? LlmConfidence { get; set; }

    // ─── State ─────────────────────────────────────────────────────────────
    public IdentificationStatus IdentificationStatus { get; set; } = IdentificationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastMetadataRefreshedAt { get; set; }

    // ─── Navigation ────────────────────────────────────────────────────────
    public ICollection<MediaItemTag> Tags { get; set; } = [];
}
