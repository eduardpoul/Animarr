namespace Animarr.Shared.Models;

/// <summary>
/// Lean projection of a <c>MediaItem</c> entity for transport over the wire.
/// Same fields the catalog grid + MediaDetail page consume, with no EF nav
/// properties or change-tracking baggage.
///
/// Image paths are server-absolute filesystem paths that have to be wrapped
/// through <c>/api/image?path=…</c> on the client — the UI helper
/// <c>ImagePath()</c> does that conversion.
/// </summary>
public sealed record MediaItemDto
{
    public Guid Id { get; init; }
    public Guid FolderId { get; init; }

    // ─── Identity ────────────────────────────────────────────────────────
    public string  Title          { get; init; } = string.Empty;
    public string? OriginalTitle  { get; init; }
    public string? CjkTitle       { get; init; }
    public string? EnglishTitle   { get; init; }
    public int?    Year           { get; init; }
    public MediaItemType MediaType { get; init; } = MediaItemType.Unknown;

    // ─── External IDs ────────────────────────────────────────────────────
    public int?    TmdbId { get; init; }
    public int?    MalId  { get; init; }
    public string? ImdbId { get; init; }
    public int?    TvdbId { get; init; }

    // ─── Image paths (absolute on disk — wrap through /api/image) ────────
    public string? PosterPath { get; init; }
    public string? FanartPath { get; init; }
    public string? LogoPath   { get; init; }

    // ─── Metadata ────────────────────────────────────────────────────────
    public string?  Description    { get; init; }
    public string?  Tagline        { get; init; }
    public string[] Genres         { get; init; } = Array.Empty<string>();
    public string[] Tags           { get; init; } = Array.Empty<string>();
    public double?  Rating         { get; init; }
    public int?     RatingCount    { get; init; }
    public double?  Popularity     { get; init; }
    public string?  Status         { get; init; }
    public string?  ContentRating  { get; init; }
    public int?     Runtime        { get; init; }
    public string?  Language       { get; init; }
    public string?  Studio         { get; init; }
    public int?     EpisodeCount   { get; init; }
    public string?  SeasonLabel    { get; init; }
    public int?     Hue            { get; init; }

    /// <summary>Per-season summaries used for the season picker.</summary>
    public SeasonMetaDto[] Seasons { get; init; } = Array.Empty<SeasonMetaDto>();

    /// <summary>Only populated when <see cref="IdentificationStatus"/> is
    /// <c>NeedsReview</c> — top-N candidates the user can pick from.</summary>
    public IdentificationCandidateDto[] Candidates { get; init; }
        = Array.Empty<IdentificationCandidateDto>();

    // ─── Per-source confidence (0..1) ────────────────────────────────────
    public double? TmdbConfidence { get; init; }
    public double? MalConfidence  { get; init; }
    public double? ImdbConfidence { get; init; }

    public string? LlmIdentifiedTitle { get; init; }
    public double? LlmConfidence      { get; init; }

    // ─── State ───────────────────────────────────────────────────────────
    public IdentificationStatus IdentificationStatus { get; init; }
    public DateTime  CreatedAt               { get; init; }
    public DateTime? LastMetadataRefreshedAt { get; init; }

    /// <summary>Tag IDs the item belongs to (matches <see cref="MediaTagDto.Id"/>).</summary>
    public Guid[] TagIds { get; init; } = Array.Empty<Guid>();
}

public sealed record SeasonMetaDto(
    int Number,
    int EpisodeCount,
    string? PosterPath,
    string? Overview,
    string? AirDate);

/// <summary>
/// One candidate match returned during identification — TMDB/MAL/IMDB row
/// presented to the user when the auto-pick confidence is below the
/// "auto-apply" threshold.
/// </summary>
public sealed record IdentificationCandidateDto(
    string Source,         // "tmdb_tv" / "tmdb_movie" / "mal" / "imdb"
    string ExternalId,     // string so MAL int + IMDB tt-prefixed both fit
    string Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,     // remote URL — UI fetches direct from TMDB CDN
    string? Overview,
    double Confidence);
