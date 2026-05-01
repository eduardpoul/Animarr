using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Lightweight client for the unofficial imdbapi.dev API.
/// No authentication required. Used as a search source for identification.
/// </summary>
public class ImdbSearchClient(IHttpClientFactory httpFactory, ILogger<ImdbSearchClient> logger)
{
    private const string ClientName = "imdb_search";

    public async Task<List<ImdbTitleResult>> SearchTitlesAsync(
        string query, int limit = 5, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            var url = $"/search/titles?query={Uri.EscapeDataString(query)}&limit={limit}";
            var response = await http.GetFromJsonAsync<ImdbSearchResponse>(url, ct);
            return response?.Titles ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IMDb] Search failed for query '{Query}'", query);
            return [];
        }
    }

    /// <summary>Fetch full title details from /titles/{imdbId} (no API key required).</summary>
    public async Task<ImdbTitleDetail?> GetTitleAsync(string imdbId, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            return await http.GetFromJsonAsync<ImdbTitleDetail>($"/titles/{Uri.EscapeDataString(imdbId)}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IMDb] GetTitle failed for '{ImdbId}'", imdbId);
            return null;
        }
    }
}

public sealed class ImdbSearchResponse
{
    [JsonPropertyName("titles")]
    public List<ImdbTitleResult> Titles { get; set; } = [];
}

public sealed class ImdbTitleResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>"tvSeries", "movie", "tvMiniSeries", etc.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("primaryTitle")]
    public string PrimaryTitle { get; set; } = "";

    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("startYear")]
    public int? StartYear { get; set; }

    [JsonPropertyName("rating")]
    public ImdbRating? Rating { get; set; }
}

/// <summary>Full detail from GET /titles/{id}.</summary>
public sealed class ImdbTitleDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("primaryTitle")]
    public string PrimaryTitle { get; set; } = "";

    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("startYear")]
    public int? StartYear { get; set; }

    [JsonPropertyName("endYear")]
    public int? EndYear { get; set; }

    [JsonPropertyName("plot")]
    public string? Plot { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("runtimeSeconds")]
    public int? RuntimeSeconds { get; set; }

    [JsonPropertyName("primaryImage")]
    public ImdbImage? PrimaryImage { get; set; }

    [JsonPropertyName("rating")]
    public ImdbRating? Rating { get; set; }
}

public sealed class ImdbImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class ImdbRating
{
    [JsonPropertyName("aggregateRating")]
    public double AggregateRating { get; set; }

    [JsonPropertyName("voteCount")]
    public int VoteCount { get; set; }
}
