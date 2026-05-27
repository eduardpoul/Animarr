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
}
