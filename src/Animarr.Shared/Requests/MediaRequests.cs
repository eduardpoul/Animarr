using Animarr.Shared.Models;

namespace Animarr.Shared.Requests;

/// <summary>User-edited fields written from EditMetadataDrawer. Null = no change.</summary>
public sealed record UpdateMediaItemRequest(
    string?  Title,
    string?  OriginalTitle,
    string?  CjkTitle,
    string?  EnglishTitle,
    int?     Year,
    MediaItemType? MediaType,
    string?  Description,
    string?  Tagline,
    string[]? Genres,
    string[]? Tags,
    string?  Status,
    string?  ContentRating,
    int?     Runtime,
    string?  Language,
    string?  Studio,
    string?  SeasonLabel,
    int?     Hue,
    Guid[]?  TagIds,
    string?  PosterPath,
    string?  FanartPath,
    string?  LogoPath);

/// <summary>Apply one of the candidates from <see cref="MediaItemDto.Candidates"/>.</summary>
public sealed record ResolveCandidateRequest(string Source, string ExternalId);

/// <summary>Filter / sort options the catalog grid hands to GET /api/media.</summary>
public sealed record MediaListQuery
{
    public string? Tag    { get; init; }
    public string? Search { get; init; }
    public MediaItemType? Type { get; init; }
    public Guid? FolderId { get; init; }
    /// <summary>Category filter by name — e.g. "Anime". Matches <see cref="Models.CategoryDto.Name"/>.</summary>
    public string? Category { get; init; }
    /// <summary>Category filter by id — alternative to <see cref="Category"/> when the
    /// client already has the Guid handy (chip-strip click).</summary>
    public Guid? CategoryId { get; init; }
    public int? Skip   { get; init; }
    public int? Take   { get; init; }
    /// <summary>"recent" (default), "title", "rating", "year".</summary>
    public string? Sort { get; init; }

    /// <summary>Response shape. <c>null</c> (default) = the full
    /// <see cref="Models.MediaItemDto"/>. <see cref="CardView"/> = only what the
    /// list screens read (poster grid, Home hero, folder stats, movie-folder
    /// filter): ids, titles, year, type, art paths, description + keywords (hero),
    /// rating, studio/language, episode count, hue, LLM confidence,
    /// identification status, refresh stamp, categories, favourite. Dropped:
    /// identification candidates, seasons, genres, tagline, theme, external ids,
    /// confidences, filler/recap flags. The response still reads as
    /// <c>MediaItemDto[]</c> (absent fields keep the DTO defaults); servers
    /// older than this option ignore it and send the full DTO. Server side:
    /// <c>MediaMappings.ToCardDto</c> — add a field there when a list screen
    /// starts reading it.</summary>
    public string? View { get; init; }

    /// <summary>Value of <see cref="View"/> for the slim list shape.</summary>
    public const string CardView = "card";
}
