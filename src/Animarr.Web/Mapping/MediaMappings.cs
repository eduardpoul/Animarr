using System.Text.Json;
using Animarr.Shared.Models;
using EntityMediaItem = Animarr.Web.Data.Models.MediaItem;
using EntityMediaTag  = Animarr.Web.Data.Models.MediaTag;

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
