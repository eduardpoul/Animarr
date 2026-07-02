using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Minimal AniList GraphQL client (https://graphql.anilist.co) — free, no auth.
/// Used only to bridge a title → MyAnimeList id, so AnimeThemes (which keys on
/// MAL/AniList ids) can be queried for anime we identified via TMDB (which
/// carries no MAL id).
/// </summary>
public class AniListClient(IHttpClientFactory httpFactory, ILogger<AniListClient> logger)
{
    private const string ClientName = "anilist";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record AniListMatch(int AniListId, int? IdMal, string? Title);

    /// <summary>Airing snapshot for one media: RELEASING/FINISHED/…, plus the
    /// next episode's number and unix air time when the show is releasing.</summary>
    public sealed record AniListAiring(
        int AniListId, int? IdMal, string? Status,
        int? NextEpisode, long? NextAiringAtUnix);

    /// <summary>Resolve an anime by title (romaji/english). Returns the AniList id
    /// plus the MAL id when AniList has the cross-reference. Null when no match.</summary>
    public async Task<AniListMatch?> ResolveAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            const string query = "query($search:String){Media(search:$search,type:ANIME){id idMal title{romaji english}}}";
            var payload = new { query, variables = new { search = title } };

            using var resp = await http.PostAsJsonAsync("/", payload, _json, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var data = await resp.Content.ReadFromJsonAsync<AniListEnvelope>(_json, ct);
            var m = data?.Data?.Media;
            if (m is null) return null;
            return new AniListMatch(m.Id, m.IdMal, m.Title?.English ?? m.Title?.Romaji);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AniList] resolve failed for '{Title}'", title);
            return null;
        }
    }

    /// <summary>Batch airing lookup — one Page query covers up to 50 titles, so
    /// a whole library of ongoings costs a single request per tick. Query by
    /// AniList ids and/or MAL ids (donghua usually exist on AniList WITHOUT a
    /// MAL cross-reference, so the id_in arm matters).</summary>
    public async Task<List<AniListAiring>> GetAiringBatchAsync(
        IReadOnlyCollection<int> aniListIds, IReadOnlyCollection<int> malIds, CancellationToken ct = default)
    {
        var result = new List<AniListAiring>();
        if (aniListIds.Count == 0 && malIds.Count == 0) return result;
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            const string query = """
                query($ids:[Int],$malIds:[Int]){
                  byId: Page(perPage:50){ media(id_in:$ids, type:ANIME){
                    id idMal status episodes nextAiringEpisode{ episode airingAt } } }
                  byMal: Page(perPage:50){ media(idMal_in:$malIds, type:ANIME){
                    id idMal status episodes nextAiringEpisode{ episode airingAt } } }
                }
                """;
            var payload = new
            {
                query,
                variables = new
                {
                    ids    = aniListIds.Count > 0 ? aniListIds.Take(50).ToArray() : null,
                    malIds = malIds.Count > 0 ? malIds.Take(50).ToArray() : null,
                },
            };

            using var resp = await http.PostAsJsonAsync("/", payload, _json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("[AniList] airing batch returned {Status}", resp.StatusCode);
                return result;
            }

            var data = await resp.Content.ReadFromJsonAsync<AniListAiringEnvelope>(_json, ct);
            var media = (data?.Data?.ById?.Media ?? [])
                .Concat(data?.Data?.ByMal?.Media ?? [])
                .GroupBy(m => m.Id)
                .Select(g => g.First());
            foreach (var m in media)
                result.Add(new AniListAiring(m.Id, m.IdMal, m.Status,
                    m.NextAiringEpisode?.Episode, m.NextAiringEpisode?.AiringAt));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AniList] airing batch failed");
            return result;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class AniListEnvelope
{
    [JsonPropertyName("data")] public AniListData? Data { get; set; }
}

public sealed class AniListData
{
    [JsonPropertyName("Media")] public AniListMedia? Media { get; set; }
}

public sealed class AniListMedia
{
    [JsonPropertyName("id")]    public int     Id    { get; set; }
    [JsonPropertyName("idMal")] public int?    IdMal { get; set; }
    [JsonPropertyName("title")] public AniListTitle? Title { get; set; }
}

public sealed class AniListTitle
{
    [JsonPropertyName("romaji")]  public string? Romaji  { get; set; }
    [JsonPropertyName("english")] public string? English { get; set; }
}

// ── airing batch DTOs ────────────────────────────────────────────────────────

public sealed class AniListAiringEnvelope
{
    [JsonPropertyName("data")] public AniListAiringData? Data { get; set; }
}

public sealed class AniListAiringData
{
    [JsonPropertyName("byId")]  public AniListPage? ById  { get; set; }
    [JsonPropertyName("byMal")] public AniListPage? ByMal { get; set; }
}

public sealed class AniListPage
{
    [JsonPropertyName("media")] public List<AniListAiringMedia>? Media { get; set; }
}

public sealed class AniListAiringMedia
{
    [JsonPropertyName("id")]       public int     Id       { get; set; }
    [JsonPropertyName("idMal")]    public int?    IdMal    { get; set; }
    [JsonPropertyName("status")]   public string? Status   { get; set; }
    [JsonPropertyName("episodes")] public int?    Episodes { get; set; }
    [JsonPropertyName("nextAiringEpisode")] public AniListNextAiring? NextAiringEpisode { get; set; }
}

public sealed class AniListNextAiring
{
    [JsonPropertyName("episode")]  public int  Episode  { get; set; }
    [JsonPropertyName("airingAt")] public long AiringAt { get; set; }
}
