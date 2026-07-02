namespace Animarr.Web.Data.Models;

public enum MediaItemType
{
    Unknown     = 0,
    Anime       = 1,
    Series      = 2,
    Movie       = 3,
    Multserials = 4,
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
/// Image paths are absolute cache paths managed by MediaCachePaths.
/// </summary>
public class MediaItem
{
    public Guid Id { get; set; }

    public Guid FolderId { get; set; }
    public FolderWatcher Folder { get; set; } = null!;

    // ─── Identity ──────────────────────────────────────────────────────────
    /// <summary>Display title, normally in the user's locale (English unless overridden).</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Original title as published by the source (e.g. romanji "Shingeki no Kyojin").</summary>
    public string? OriginalTitle { get; set; }
    /// <summary>CJK-script title (Hanzi/Kanji/Hangul). Separate from OriginalTitle because the
    /// original can be romanji while CjkTitle is always native script. Used for the hero CJK watermark.</summary>
    public string? CjkTitle { get; set; }
    /// <summary>English title when distinct from Title. Filled when the source's preferred English
    /// alternative differs from the display title (common for MAL / Donghua releases).</summary>
    public string? EnglishTitle { get; set; }
    public int? Year { get; set; }
    public MediaItemType MediaType { get; set; } = MediaItemType.Unknown;

    // ─── External IDs ──────────────────────────────────────────────────────
    public int?    TmdbId { get; set; }
    public int?    MalId  { get; set; }
    public string? ImdbId { get; set; }
    public int?    TvdbId { get; set; }
    /// <summary>AniList id, captured whenever the AniList bridge resolves this
    /// title (theme music / AniSkip MAL-id lookups). Unlike MalId it exists for
    /// most donghua too, and is the key for future airing-schedule / relations
    /// queries against the AniList GraphQL API.</summary>
    public int?    AniListId { get; set; }

    // ─── Local image paths (absolute paths to MediaCachePaths.ForFolder dir) ─
    /// <summary>poster.jpg — primary poster art (portrait)</summary>
    public string? PosterPath { get; set; }
    /// <summary>fanart.jpg — wide backdrop used as hero/background</summary>
    public string? FanartPath { get; set; }
    /// <summary>logo.png — transparent title logo (if available from TMDB)</summary>
    public string? LogoPath { get; set; }

    // ─── Theme music (anime OP/ED) ───────────────────────────────────────────
    /// <summary>theme.ogg — anime opening/ending theme. Absolute path under the
    /// media folder's <c>.animarr/&lt;folderId&gt;/</c> dir — NOT the central image
    /// cache: kept next to the media so the Docker data volume doesn't balloon
    /// with audio. null = none / not fetched (or source had no theme).</summary>
    public string? ThemePath { get; set; }
    /// <summary>Theme song label for display ("Syoudou — Itou Kanako"). From
    /// AnimeThemes <c>song.title</c> (+ artist). null when unknown.</summary>
    public string? ThemeTitle { get; set; }

    // ─── Metadata ──────────────────────────────────────────────────────────
    public string? Description { get; set; }
    public string? Tagline { get; set; }
    /// <summary>JSON array of genre names in canonical English: ["Action","Drama"].
    /// Catalog logic (anime detection, category classification, theme matching) keys
    /// off these, so they stay language-independent regardless of the metadata language.</summary>
    public string? GenresJson { get; set; }
    /// <summary>JSON array of the same genres localized to the metadata language, for
    /// display only. Null when the language is English (UI falls back to <see cref="GenresJson"/>).</summary>
    public string? GenresLocalizedJson { get; set; }
    /// <summary>JSON array of descriptive tag strings: ["Donghua","Cultivation","Mecha"].
    /// Distinct from MediaTag/MediaItemTag (which models user collections); these are
    /// source-derived labels used by hero/poster overlays.</summary>
    public string? TagsJson { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }
    /// <summary>Popularity score from TMDB / MAL (>0). Used for candidate scoring tiebreakers.</summary>
    public double? Popularity { get; set; }
    /// <summary>e.g. "Ended", "Returning Series", "Canceled", "Finished Airing"</summary>
    public string? Status { get; set; }
    /// <summary>Age rating: PG-13, R, TV-MA, PG, etc.</summary>
    public string? ContentRating { get; set; }
    /// <summary>Runtime in minutes (per-episode for series, total for movies).</summary>
    public int? Runtime { get; set; }
    /// <summary>Original audio language as display name ("Mandarin", "Japanese", "English").
    /// Derived from source ISO-639-1 code via LanguageNameMap.</summary>
    public string? Language { get; set; }
    /// <summary>Production studio or network ("Fortiche", "Sunrise", "Bilibili", "HBO").
    /// Sourced from TMDB networks/production_companies or MAL studios.</summary>
    public string? Studio { get; set; }
    /// <summary>Denormalised count of episodes across all seasons. Cached for the catalog grid
    /// which renders 200+ posters and shouldn't deserialize SeasonsJson per card.</summary>
    public int? EpisodeCount { get; set; }
    /// <summary>Display label shown under the poster ("TV-1", "S2", "Movie"). Auto-derived from
    /// MediaType + season count when null.</summary>
    public string? SeasonLabel { get; set; }
    /// <summary>Hue in degrees (0..360) used for poster wash + CJK watermark color.
    /// Deterministically derived from Title hash on first identify; user-editable.</summary>
    public int? Hue { get; set; }
    /// <summary>
    /// JSON array of SeasonMeta objects:
    /// [{"number":1,"episodeCount":13,"posterPath":"season01/poster.jpg","overview":"...","airDate":"2013-04-07"}]
    /// </summary>
    public string? SeasonsJson { get; set; }
    /// <summary>
    /// JSON map diskSeason→absoluteOffset for donghua/anime where TMDB lists the
    /// whole run as one absolute season but the disk is split into seasons.
    /// e.g. {"3":106} means disk Season 3 Episode 1 = TMDB absolute episode 107.
    /// Computed by SeasonOffsetResolver from TMDB air-date gaps; applied on the
    /// fly in MediaFileResolver to fill MediaFileDto.AbsoluteEpisode. Null when
    /// not applicable (TMDB already multi-season, or single-season disk).
    /// </summary>
    public string? SeasonOffsetsJson { get; set; }
    /// <summary>
    /// JSON array of top-3 search candidates when status is NeedsReview.
    /// Cleared after user picks one.
    /// </summary>
    public string? CandidatesJson { get; set; }

    // ─── Per-source match confidence (0..1) ───────────────────────────────
    /// <summary>TMDB match confidence — typically based on vote_count + title-similarity.
    /// Falls back to LlmConfidence when null.</summary>
    public double? TmdbConfidence { get; set; }
    public double? MalConfidence  { get; set; }
    public double? ImdbConfidence { get; set; }

    // ─── LLM ───────────────────────────────────────────────────────────────
    /// <summary>Title extracted by LLM before external search (for debugging/display)</summary>
    public string? LlmIdentifiedTitle { get; set; }
    public double? LlmConfidence { get; set; }

    // ─── State ─────────────────────────────────────────────────────────────
    public IdentificationStatus IdentificationStatus { get; set; } = IdentificationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastMetadataRefreshedAt { get; set; }
    /// <summary>When the skip-intro/credits background pass last analysed this
    /// item with the heavy providers (chromaprint). Null = never scanned. Gates
    /// the background queue so a title is fully scanned once, then only
    /// re-scanned after a long TTL.</summary>
    public DateTime? LastSegmentScanAt { get; set; }
    /// <summary>When the trickplay background pass last generated seek-preview
    /// sprites for this item. Same gating contract as LastSegmentScanAt: null =
    /// never, cleared by the torrent-completion hook so new episodes of an
    /// ongoing title get sprites without waiting out the TTL.</summary>
    public DateTime? LastTrickplayScanAt { get; set; }

    // ─── Airing schedule (ongoing calendar) ────────────────────────────────
    // Refreshed by AiringRefreshBackgroundService: AniList nextAiringEpisode
    // for anime/donghua (batched), TMDB next_episode_to_air as the fallback
    // for live-action "Returning Series".

    /// <summary>Normalized release state: RELEASING / NOT_YET_RELEASED /
    /// FINISHED / CANCELLED / HIATUS (AniList vocabulary; TMDB statuses are
    /// mapped onto it). Null = never checked.</summary>
    public string? AiringStatus { get; set; }
    /// <summary>Absolute number of the NEXT episode to air (source numbering —
    /// AniList counts per-entry, TMDB per-season; display maps via
    /// SeasonOffsetsJson where applicable).</summary>
    public int? NextEpisodeNumber { get; set; }
    public DateTime? NextAirAtUtc { get; set; }
    /// <summary>The previously-announced episode once its air time passes —
    /// keeps "aired recently, is the file here yet?" answerable after the
    /// refresh pass rolls NextEpisode forward.</summary>
    public int? LastAiredEpisode { get; set; }
    public DateTime? LastAiredAtUtc { get; set; }
    /// <summary>Refresh gate: re-checked when stale (12h) or when the stored
    /// NextAirAtUtc has passed. Null = never checked.</summary>
    public DateTime? LastAiringCheckAt { get; set; }

    // ─── Navigation ────────────────────────────────────────────────────────
    public ICollection<MediaItemTag> Tags { get; set; } = [];
    public ICollection<MediaItemCategory> Categories { get; set; } = [];
}
