using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// MyAnimeList API v2 client.
/// Requires a Client-ID in the X-MAL-CLIENT-ID header (no OAuth needed for public search).
/// </summary>
public class MalClient(IHttpClientFactory httpFactory, ILogger<MalClient> logger)
{
    private const string BaseUrl = "https://api.myanimelist.net/v2";

    private const string DefaultFields =
        "id,title,main_picture,synopsis,genres,mean,num_episodes,status," +
        "start_date,end_date,media_type,alternative_titles,rating,num_scoring_users," +
        "studios,start_season,source,broadcast,nsfw,popularity,average_episode_duration," +
        "pictures";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Search ───────────────────────────────────────────────────────────────

    public async Task<List<MalAnime>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            using var http = httpFactory.CreateClient("mal");
            var url = $"{BaseUrl}/anime?q={Uri.EscapeDataString(query)}&limit={limit}&fields={DefaultFields}";
            var resp = await http.GetFromJsonAsync<MalPagedResponse>(url, _json, ct);
            return resp?.Data?.Select(n => n.Node).ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MAL search failed for '{Query}'", query);
            return [];
        }
    }

    // ── Detail ───────────────────────────────────────────────────────────────

    public async Task<MalAnime?> GetDetailAsync(int malId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("mal");
            var url = $"{BaseUrl}/anime/{malId}?fields={DefaultFields}";
            return await http.GetFromJsonAsync<MalAnime>(url, _json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MAL GetDetail failed for id={Id}", malId);
            return null;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class MalPagedResponse
{
    public List<MalNodeWrapper> Data { get; set; } = [];
}

public class MalNodeWrapper
{
    public MalAnime Node { get; set; } = new();
}

public class MalAnime
{
    public int Id { get; set; }
    public string Title { get; set; } = "";

    [JsonPropertyName("main_picture")]
    public MalPicture? MainPicture { get; set; }

    /// <summary>Additional poster candidates beyond MainPicture. Returned only when `pictures` is requested.</summary>
    public List<MalPicture> Pictures { get; set; } = [];

    [JsonPropertyName("alternative_titles")]
    public MalAlternativeTitles? AlternativeTitles { get; set; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }

    [JsonPropertyName("end_date")]
    public string? EndDate { get; set; }

    public string? Synopsis { get; set; }
    public double? Mean { get; set; }
    public double? Popularity { get; set; }

    [JsonPropertyName("num_scoring_users")]
    public int? NumScoringUsers { get; set; }

    [JsonPropertyName("num_episodes")]
    public int? NumEpisodes { get; set; }

    [JsonPropertyName("average_episode_duration")]
    /// <summary>Episode duration in SECONDS (MAL convention). Convert to minutes via /60 for display.</summary>
    public int? AverageEpisodeDuration { get; set; }

    public string? Status { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    public List<MalGenreWrapper> Genres { get; set; } = [];
    public List<MalStudio> Studios { get; set; } = [];

    [JsonPropertyName("start_season")]
    public MalSeason? StartSeason { get; set; }

    public MalBroadcast? Broadcast { get; set; }

    /// <summary>Source material: "manga", "light_novel", "original", "novel", "web_manga", etc.</summary>
    public string? Source { get; set; }

    public string? Rating { get; set; }
    public bool? Nsfw { get; set; }

    public int? Year => int.TryParse((StartDate ?? "").Split('-')[0], out var y) ? y : null;
    public string EnglishTitle => AlternativeTitles?.En ?? Title;
    public string? PosterUrl => MainPicture?.Large ?? MainPicture?.Medium;
    public string? StudioName => Studios.FirstOrDefault()?.Name;
    /// <summary>Runtime per episode in minutes (MAL stores seconds).</summary>
    public int? RuntimeMinutes => AverageEpisodeDuration.HasValue ? AverageEpisodeDuration.Value / 60 : null;
}

public class MalPicture
{
    public string? Medium { get; set; }
    public string? Large  { get; set; }
}

public class MalAlternativeTitles
{
    public string? En { get; set; }
    public string? Ja { get; set; }
    public List<string> Synonyms { get; set; } = [];
}

public class MalGenreWrapper
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class MalStudio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class MalSeason
{
    public int Year { get; set; }
    /// <summary>"winter" | "spring" | "summer" | "fall"</summary>
    public string Season { get; set; } = "";
}

public class MalBroadcast
{
    [JsonPropertyName("day_of_the_week")] public string? DayOfTheWeek { get; set; }
    [JsonPropertyName("start_time")]      public string? StartTime    { get; set; }
}
