using System.Linq.Expressions;
using System.Text.Json;
using Animarr.Shared.Models;
using EntityMediaItem = Animarr.Web.Data.Models.MediaItem;
using EntityMediaTag  = Animarr.Web.Data.Models.MediaTag;
using EntityMediaType = Animarr.Web.Data.Models.MediaItemType;
using EntityIdStatus  = Animarr.Web.Data.Models.IdentificationStatus;

namespace Animarr.Web.Mapping;

/// <summary>
/// Converters between server-side EF entities and Animarr.Shared transport
/// DTOs. Centralised here so endpoint handlers stay terse — every endpoint
/// reduces to "load + map + return" or "validate + persist + map + return".
///
/// The entities and DTOs share field names but live in separate namespaces
/// (the EF entity has nav properties + change-tracking; the DTO is a flat
/// record). We deliberately don't auto-generate these because the JSON
/// columns (Genres/Tags/Seasons/Candidates) need bespoke parsing and a
/// generic mapper would lose that.
/// </summary>
internal static class MediaMappings
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static MediaItemDto ToDto(this EntityMediaItem entity,
        HashSet<Guid>? favoriteIds = null)
    {
        var flags = DeserialiseEpisodeFlags(entity.EpisodeFlagsJson);
        return new MediaItemDto
        {
            IsFavorite      = favoriteIds is not null && favoriteIds.Contains(entity.Id),
            Id              = entity.Id,
            FolderId        = entity.FolderId,
            Title           = entity.Title,
            OriginalTitle   = entity.OriginalTitle,
            CjkTitle        = entity.CjkTitle,
            EnglishTitle    = entity.EnglishTitle,
            Year            = entity.Year,
            MediaType       = (Animarr.Shared.MediaItemType)(int)entity.MediaType,
            TmdbId          = entity.TmdbId,
            MalId           = entity.MalId,
            ImdbId          = entity.ImdbId,
            TvdbId          = entity.TvdbId,
            PosterPath      = entity.PosterPath,
            FanartPath      = entity.FanartPath,
            LogoPath        = entity.LogoPath,
            ThemePath       = entity.ThemePath,
            ThemeTitle      = entity.ThemeTitle,
            Description     = entity.Description,
            Tagline         = entity.Tagline,
            Genres          = DeserialiseStrings(entity.GenresJson),
            GenresLocalized = DeserialiseStrings(entity.GenresLocalizedJson),
            Tags            = DeserialiseStrings(entity.TagsJson),
            Rating          = entity.Rating,
            RatingCount     = entity.RatingCount,
            Popularity      = entity.Popularity,
            Status          = entity.Status,
            ContentRating   = entity.ContentRating,
            Runtime         = entity.Runtime,
            Language        = entity.Language,
            Studio          = entity.Studio,
            EpisodeCount    = entity.EpisodeCount,
            SeasonLabel     = entity.SeasonLabel,
            Hue             = entity.Hue,
            Seasons         = DeserialiseSeasons(entity.SeasonsJson),
            Candidates      = DeserialiseCandidates(entity.CandidatesJson),
            TmdbConfidence  = entity.TmdbConfidence,
            MalConfidence   = entity.MalConfidence,
            ImdbConfidence  = entity.ImdbConfidence,
            LlmIdentifiedTitle = entity.LlmIdentifiedTitle,
            LlmConfidence      = entity.LlmConfidence,
            IdentificationStatus = (Animarr.Shared.IdentificationStatus)(int)entity.IdentificationStatus,
            CreatedAt        = entity.CreatedAt,
            LastMetadataRefreshedAt = entity.LastMetadataRefreshedAt,
            TagIds          = entity.Tags.Select(t => t.MediaTagId).ToArray(),
            CategoryIds     = entity.Categories.Select(c => c.CategoryId).ToArray(),
            CategoryNames   = entity.Categories
                                .Where(c => c.Category is not null)
                                .OrderBy(c => c.Category!.SortOrder)
                                .Select(c => c.Category!.Name)
                                .ToArray(),
            FillerEpisodes  = flags.Filler,
            RecapEpisodes   = flags.Recap,
        };
    }

    // ─── Catalog card view (GET /api/media?view=card) ────────────────────
    //
    // Every list screen (Home grid + hero, Library, Search, Settings → Folders,
    // the Downloads movie-folder filter, TV Home / Library) used to receive the
    // full DTO for the whole library; ~60 % of those bytes were fields no list
    // reads (identification candidates with their overviews alone were 43 %).
    // The card view keeps exactly the fields those screens read — keep it in
    // sync with them (Poster.razor, CatalogHero.razor, the pages' filters, TV
    // CatalogNativePage/LibraryAllPage ApiItem records).

    /// <summary>SQL projection for the card view: only the columns
    /// <see cref="ToCardDto"/> reads, so the heavy text/JSON columns
    /// (CandidatesJson, SeasonsJson, EpisodeFlagsJson, genres, tagline, …)
    /// are never read off disk. Categories come back as a sub-collection
    /// (run it with AsSplitQuery so the item columns aren't repeated per
    /// category).</summary>
    public static readonly Expression<Func<EntityMediaItem, MediaCardRow>> CardRow = m => new MediaCardRow
    {
        Id                      = m.Id,
        FolderId                = m.FolderId,
        Title                   = m.Title,
        OriginalTitle           = m.OriginalTitle,
        CjkTitle                = m.CjkTitle,
        EnglishTitle            = m.EnglishTitle,
        Year                    = m.Year,
        MediaType               = m.MediaType,
        PosterPath              = m.PosterPath,
        FanartPath              = m.FanartPath,
        Description             = m.Description,
        TagsJson                = m.TagsJson,
        Rating                  = m.Rating,
        RatingCount             = m.RatingCount,
        Language                = m.Language,
        Studio                  = m.Studio,
        EpisodeCount            = m.EpisodeCount,
        Hue                     = m.Hue,
        LlmConfidence           = m.LlmConfidence,
        IdentificationStatus    = m.IdentificationStatus,
        LastMetadataRefreshedAt = m.LastMetadataRefreshedAt,
        Categories              = m.Categories
            .Select(c => new MediaCardCategory(c.CategoryId, c.Category.Name, c.Category.SortOrder))
            .ToList(),
    };

    public static MediaCardDto ToCardDto(this MediaCardRow row, HashSet<Guid>? favoriteIds = null) => new()
    {
        IsFavorite              = favoriteIds is not null && favoriteIds.Contains(row.Id),
        Id                      = row.Id,
        FolderId                = row.FolderId,
        Title                   = row.Title,
        OriginalTitle           = row.OriginalTitle,
        CjkTitle                = row.CjkTitle,
        EnglishTitle            = row.EnglishTitle,
        Year                    = row.Year,
        MediaType               = (Animarr.Shared.MediaItemType)(int)row.MediaType,
        PosterPath              = row.PosterPath,
        FanartPath              = row.FanartPath,
        Description             = row.Description,
        Tags                    = DeserialiseStrings(row.TagsJson),
        Rating                  = row.Rating,
        RatingCount             = row.RatingCount,
        Language                = row.Language,
        Studio                  = row.Studio,
        EpisodeCount            = row.EpisodeCount,
        Hue                     = row.Hue,
        LlmConfidence           = row.LlmConfidence,
        IdentificationStatus    = (Animarr.Shared.IdentificationStatus)(int)row.IdentificationStatus,
        LastMetadataRefreshedAt = row.LastMetadataRefreshedAt,
        // Same order rules as ToDto: ids in load order, names by SortOrder.
        CategoryIds             = row.Categories.Select(c => c.CategoryId).ToArray(),
        CategoryNames           = row.Categories.OrderBy(c => c.SortOrder).Select(c => c.Name).ToArray(),
    };

    public static MediaTagDto ToDto(this EntityMediaTag tag, int itemCount)
        => new(tag.Id, tag.Name, tag.Color, tag.SortOrder, tag.IsAutoTag, itemCount);

    // ─── JSON column helpers ─────────────────────────────────────────────

    private static string[] DeserialiseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static string? SerialiseStrings(string[]? values)
    {
        if (values is null || values.Length == 0) return null;
        return JsonSerializer.Serialize(values, JsonOpts);
    }

    private static SeasonMetaDto[] DeserialiseSeasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SeasonMetaDto>();
        try
        {
            return JsonSerializer.Deserialize<SeasonMetaDto[]>(json, JsonOpts) ?? Array.Empty<SeasonMetaDto>();
        }
        catch { return Array.Empty<SeasonMetaDto>(); }
    }

    private static (int[] Filler, int[] Recap) DeserialiseEpisodeFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (Array.Empty<int>(), Array.Empty<int>());
        try
        {
            var d = JsonSerializer.Deserialize<Services.EpisodeFlagsData>(json, JsonOpts);
            return (d?.Filler ?? Array.Empty<int>(), d?.Recap ?? Array.Empty<int>());
        }
        catch { return (Array.Empty<int>(), Array.Empty<int>()); }
    }

    private static IdentificationCandidateDto[] DeserialiseCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<IdentificationCandidateDto>();
        try
        {
            return JsonSerializer.Deserialize<IdentificationCandidateDto[]>(json, JsonOpts)
                ?? Array.Empty<IdentificationCandidateDto>();
        }
        catch { return Array.Empty<IdentificationCandidateDto>(); }
    }
}

/// <summary>Row shape of <see cref="MediaMappings.CardRow"/> (SQL projection).</summary>
internal sealed class MediaCardRow
{
    public Guid      Id                      { get; init; }
    public Guid      FolderId                { get; init; }
    public string    Title                   { get; init; } = string.Empty;
    public string?   OriginalTitle           { get; init; }
    public string?   CjkTitle                { get; init; }
    public string?   EnglishTitle            { get; init; }
    public int?      Year                    { get; init; }
    public EntityMediaType MediaType         { get; init; }
    public string?   PosterPath              { get; init; }
    public string?   FanartPath              { get; init; }
    public string?   Description             { get; init; }
    public string?   TagsJson                { get; init; }
    public double?   Rating                  { get; init; }
    public int?      RatingCount             { get; init; }
    public string?   Language                { get; init; }
    public string?   Studio                  { get; init; }
    public int?      EpisodeCount            { get; init; }
    public int?      Hue                     { get; init; }
    public double?   LlmConfidence           { get; init; }
    public EntityIdStatus IdentificationStatus { get; init; }
    public DateTime? LastMetadataRefreshedAt { get; init; }
    public List<MediaCardCategory> Categories { get; init; } = [];
}

internal sealed record MediaCardCategory(Guid CategoryId, string Name, int SortOrder);

/// <summary>
/// Wire shape of <c>GET /api/media?view=card</c>. Every property name is also
/// a <see cref="MediaItemDto"/> property with the same type, so clients keep
/// reading the response into <c>MediaItemDto[]</c> — properties left out here
/// come back as that DTO's defaults (null / empty arrays). A separate type
/// rather than a half-filled MediaItemDto so the dropped fields aren't sent as
/// ~25 <c>"x":null</c> / <c>[]</c> pairs per title.
/// </summary>
internal sealed record MediaCardDto
{
    public Guid      Id                      { get; init; }
    public Guid      FolderId                { get; init; }
    public string    Title                   { get; init; } = string.Empty;
    public string?   OriginalTitle           { get; init; }
    public string?   CjkTitle                { get; init; }
    public string?   EnglishTitle            { get; init; }
    public int?      Year                    { get; init; }
    public Animarr.Shared.MediaItemType MediaType { get; init; }
    public string?   PosterPath              { get; init; }
    public string?   FanartPath              { get; init; }
    public string?   Description             { get; init; }
    public string[]  Tags                    { get; init; } = [];
    public double?   Rating                  { get; init; }
    public int?      RatingCount             { get; init; }
    public string?   Language                { get; init; }
    public string?   Studio                  { get; init; }
    public int?      EpisodeCount            { get; init; }
    public int?      Hue                     { get; init; }
    public double?   LlmConfidence           { get; init; }
    public Animarr.Shared.IdentificationStatus IdentificationStatus { get; init; }
    public DateTime? LastMetadataRefreshedAt { get; init; }
    public Guid[]    CategoryIds             { get; init; } = [];
    public string[]  CategoryNames           { get; init; } = [];
    public bool      IsFavorite              { get; init; }
}
