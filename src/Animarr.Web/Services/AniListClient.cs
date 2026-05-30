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
