using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>Thrown when TMDB rejects a request as unauthorized — usually a missing/invalid API key.</summary>
public class TmdbAuthException(string message) : Exception(message);

/// <summary>
/// Lightweight TMDB API v3 client.
/// All image paths returned by TMDB are relative (e.g. "/abc123.jpg"); use BuildImageUrl() to build full URLs.
/// </summary>
public class TmdbClient(IHttpClientFactory httpFactory, ILogger<TmdbClient> logger)
{
    private const string BaseUrl   = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";
    private const int MaxRetriesOn429 = 3;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>H-9: GET with branching on HTTP status codes (401/403/429/5xx).
    /// 429 → respect Retry-After and back off up to 3 times.
    /// 401/403 → throw TmdbAuthException so callers can surface "bad API key" to UI.
    /// All other failures → log + return default.</summary>
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var http = httpFactory.CreateClient("tmdb");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadFromJsonAsync<T>(_json, ct);

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                    case HttpStatusCode.Forbidden:
                        logger.LogWarning("TMDB {Status} on {Url} — check API key", resp.StatusCode, url);
                        throw new TmdbAuthException($"TMDB rejected the request ({(int)resp.StatusCode}). Check that the API key is set and valid.");
                    case HttpStatusCode.TooManyRequests:
                        if (attempt >= MaxRetriesOn429)
                        {
                            logger.LogWarning("TMDB 429 — exhausted retries on {Url}", url);
                            return null;
                        }
                        var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                        logger.LogInformation("TMDB 429 — backing off {Delay}s (attempt {Attempt})", delay.TotalSeconds, attempt + 1);
                        await Task.Delay(delay, ct);
                        continue;
                    case HttpStatusCode.NotFound:
                        return null;
                    default:
                        logger.LogWarning("TMDB {Status} on {Url}", resp.StatusCode, url);
                        return null;
                }
            }
            catch (TmdbAuthException) { throw; }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TMDB request failed: {Url}", url);
                return null;
            }
        }
    }

    // ── Image helpers ────────────────────────────────────────────────────────

    public static string PosterUrl(string path, string size = "w500")   => $"{ImageBase}{size}{path}";
    public static string BackdropUrl(string path, string size = "w1280") => $"{ImageBase}{size}{path}";
    public static string LogoUrl(string path, string size = "w300")     => $"{ImageBase}{size}{path}";
    public static string StillUrl(string path, string size = "w300")    => $"{ImageBase}{size}{path}";

    // ── Search ───────────────────────────────────────────────────────────────

    public async Task<List<TmdbSearchResult>> SearchTvAsync(string query, CancellationToken ct = default)
        => await SearchAsync("search/tv", query, ct);

    public async Task<List<TmdbSearchResult>> SearchMovieAsync(string query, CancellationToken ct = default)
        => await SearchAsync("search/movie", query, ct);

    public async Task<List<TmdbSearchResult>> SearchMultiAsync(string query, CancellationToken ct = default)
        => await SearchAsync("search/multi", query, ct);

    private async Task<List<TmdbSearchResult>> SearchAsync(string endpoint, string query, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{endpoint}?query={Uri.EscapeDataString(query)}&include_adult=false&language=en-US";
        var resp = await GetJsonAsync<TmdbPagedResponse<TmdbSearchResult>>(url, ct);
        return resp?.Results ?? [];
    }

    // ── TV series detail ─────────────────────────────────────────────────────

    public Task<TmdbTvDetail?> GetTvDetailAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbTvDetail>($"{BaseUrl}/tv/{tmdbId}?append_to_response=images,content_ratings,external_ids,credits,keywords&language=en-US&include_image_language=en,ja,zh,ru,null", ct);

    public Task<TmdbSeasonDetail?> GetSeasonDetailAsync(int tmdbId, int seasonNumber, CancellationToken ct = default)
        => GetJsonAsync<TmdbSeasonDetail>($"{BaseUrl}/tv/{tmdbId}/season/{seasonNumber}?append_to_response=images&language=en-US&include_image_language=en,ja,zh,ru,null", ct);

    // ── Movie detail ─────────────────────────────────────────────────────────

    public Task<TmdbMovieDetail?> GetMovieDetailAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbMovieDetail>($"{BaseUrl}/movie/{tmdbId}?append_to_response=images,release_dates,external_ids,credits,keywords&language=en-US&include_image_language=en,ja,zh,ru,null", ct);

    // ── Translations (for CJK / English alternative titles) ──────────────────

    public Task<TmdbTranslations?> GetTvTranslationsAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbTranslations>($"{BaseUrl}/tv/{tmdbId}/translations", ct);

    public Task<TmdbTranslations?> GetMovieTranslationsAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbTranslations>($"{BaseUrl}/movie/{tmdbId}/translations", ct);

    // ── Find by external ID ──────────────────────────────────────────────────

    /// <summary>Find TMDB entries by external ID (IMDb or TVDB).</summary>
    /// <param name="externalId">e.g. "tt1234567" for IMDb, "83268" for TVDB</param>
    /// <param name="source">"imdb_id" or "tvdb_id"</param>
    public Task<TmdbFindResponse?> FindByExternalIdAsync(string externalId, string source, CancellationToken ct = default)
        => GetJsonAsync<TmdbFindResponse>($"{BaseUrl}/find/{Uri.EscapeDataString(externalId)}?external_source={source}&language=en-US", ct);

    // ── All images for a given TMDB entity ───────────────────────────────────

    /// <summary>Returns all posters, backdrops and logos for a TV series.</summary>
    public Task<TmdbImages?> GetTvImagesAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbImages>($"{BaseUrl}/tv/{tmdbId}/images?include_image_language=en,ja,ru,null", ct);

    /// <summary>Returns all posters, backdrops and logos for a movie.</summary>
    public Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbImages>($"{BaseUrl}/movie/{tmdbId}/images?include_image_language=en,ja,ru,null", ct);

    // ── Image download helper ────────────────────────────────────────────────

    public async Task<bool> DownloadImageAsync(string imageUrl, string localPath, CancellationToken ct = default)
    {
        try
        {
            // Use a plain client (no auth) — TMDB image CDN doesn't accept bearer tokens
            using var http = httpFactory.CreateClient();
            var bytes = await http.GetByteArrayAsync(imageUrl, ct);
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download image from {Url} to {Path}", imageUrl, localPath);
            return false;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class TmdbPagedResponse<T>
{
    public int Page { get; set; }
    [JsonPropertyName("total_results")] public int TotalResults { get; set; }
    public List<T> Results { get; set; } = [];
}

public class TmdbSearchResult
{
    public int Id { get; set; }
    public string? Name { get; set; }                      // TV
    public string? Title { get; set; }                     // Movie
    [JsonPropertyName("original_name")]  public string? OriginalName  { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate  { get; set; }
    [JsonPropertyName("release_date")]   public string? ReleaseDate   { get; set; }
    [JsonPropertyName("poster_path")]    public string? PosterPath    { get; set; }
    [JsonPropertyName("backdrop_path")]  public string? BackdropPath  { get; set; }
    [JsonPropertyName("media_type")]     public string? MediaType     { get; set; }
    [JsonPropertyName("vote_average")]   public double VoteAverage    { get; set; }
    [JsonPropertyName("vote_count")]     public int VoteCount         { get; set; }
    public string? Overview { get; set; }

    public string DisplayTitle => Name ?? Title ?? OriginalName ?? OriginalTitle ?? "";
    public string? DisplayDate => FirstAirDate ?? ReleaseDate;
    public int? Year => int.TryParse((DisplayDate ?? "").Split('-')[0], out var y) ? y : null;
}

public class TmdbTvDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("last_air_date")]  public string? LastAirDate  { get; set; }
    [JsonPropertyName("poster_path")]   public string? PosterPath   { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    public string? Overview { get; set; }
    public string? Tagline  { get; set; }
    public string? Status   { get; set; }
    public string? Homepage { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")]   public int    VoteCount   { get; set; }
    public double Popularity { get; set; }
    [JsonPropertyName("episode_run_time")] public List<int> EpisodeRunTime { get; set; } = [];
    [JsonPropertyName("number_of_episodes")] public int NumberOfEpisodes { get; set; }
    public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("number_of_seasons")] public int NumberOfSeasons { get; set; }
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummary> Seasons { get; set; } = [];
    [JsonPropertyName("external_ids")] public TmdbExternalIds? ExternalIds { get; set; }
    public TmdbImages? Images { get; set; }
    [JsonPropertyName("content_ratings")] public TmdbContentRatings? ContentRatings { get; set; }
    [JsonPropertyName("production_companies")] public List<TmdbCompany> ProductionCompanies { get; set; } = [];
    public List<TmdbCompany> Networks { get; set; } = [];
    [JsonPropertyName("created_by")] public List<TmdbCrewMember> CreatedBy { get; set; } = [];
    public TmdbCredits? Credits { get; set; }
    public TmdbKeywords? Keywords { get; set; }
    [JsonPropertyName("in_production")] public bool InProduction { get; set; }
    [JsonPropertyName("type")] public string? ShowType { get; set; }

    public int? Year => int.TryParse((FirstAirDate ?? "").Split('-')[0], out var y) ? y : null;
    public string? ContentRating => ContentRatings?.Results?.FirstOrDefault(r => r.Iso31661 == "US")?.Rating;
    public string? BestLogoPath  => Images?.Logos?.FirstOrDefault(i => i.Iso6391 == "en")?.FilePath
                                 ?? Images?.Logos?.FirstOrDefault()?.FilePath;
    public string? BestFanartPath => BackdropPath
                                  ?? Images?.Backdrops?.OrderByDescending(b => b.VoteAverage).FirstOrDefault()?.FilePath;
    /// <summary>Prefer Networks[0] (HBO, Bilibili, …) — users recognise these — fall back to ProductionCompanies[0].</summary>
    public string? StudioName => Networks.FirstOrDefault()?.Name ?? ProductionCompanies.FirstOrDefault()?.Name;
}

public class TmdbMovieDetail
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
    [JsonPropertyName("release_date")]   public string? ReleaseDate   { get; set; }
    [JsonPropertyName("poster_path")]    public string? PosterPath    { get; set; }
    [JsonPropertyName("backdrop_path")]  public string? BackdropPath  { get; set; }
    public string? Overview  { get; set; }
    public string? Tagline   { get; set; }
    public string? Status    { get; set; }
    public string? Homepage  { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")]   public int    VoteCount   { get; set; }
    public double Popularity { get; set; }
    public int? Runtime { get; set; }
    public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("external_ids")] public TmdbExternalIds? ExternalIds { get; set; }
    public TmdbImages? Images { get; set; }
    [JsonPropertyName("release_dates")] public TmdbReleaseDates? ReleaseDates { get; set; }
    [JsonPropertyName("production_companies")] public List<TmdbCompany> ProductionCompanies { get; set; } = [];
    public TmdbCredits? Credits { get; set; }
    public TmdbKeywords? Keywords { get; set; }

    public int? Year => int.TryParse((ReleaseDate ?? "").Split('-')[0], out var y) ? y : null;
    public string? ContentRating => ReleaseDates?.Results?.FirstOrDefault(r => r.Iso31661 == "US")
        ?.ReleaseDates?.FirstOrDefault(r => r.Certification != "")?.Certification;
    public string? BestLogoPath   => Images?.Logos?.FirstOrDefault(i => i.Iso6391 == "en")?.FilePath
                                  ?? Images?.Logos?.FirstOrDefault()?.FilePath;
    public string? StudioName => ProductionCompanies.FirstOrDefault()?.Name;
}

public class TmdbSeasonDetail
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    public List<TmdbEpisode> Episodes { get; set; } = [];
}

public class TmdbSeasonSummary
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
    [JsonPropertyName("poster_path")]   public string? PosterPath { get; set; }
}

public class TmdbEpisode
{
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("season_number")]  public int SeasonNumber  { get; set; }
    public string Name { get; set; } = "";
    public string? Overview { get; set; }
    [JsonPropertyName("still_path")]     public string? StillPath     { get; set; }
    [JsonPropertyName("air_date")]       public string? AirDate       { get; set; }
    [JsonPropertyName("vote_average")]   public double  VoteAverage   { get; set; }
    public int? Runtime { get; set; }
}

public class TmdbGenre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]     public string? ImdbId    { get; set; }
    [JsonPropertyName("tvdb_id")]     public int?    TvdbId    { get; set; }
}

public class TmdbImages
{
    public List<TmdbImage> Backdrops { get; set; } = [];
    public List<TmdbImage> Posters   { get; set; } = [];
    public List<TmdbImage> Logos     { get; set; } = [];
}

public class TmdbImage
{
    [JsonPropertyName("file_path")]    public string  FilePath    { get; set; } = "";
    [JsonPropertyName("iso_639_1")]    public string? Iso6391     { get; set; }
    [JsonPropertyName("vote_average")] public double  VoteAverage { get; set; }
}

public class TmdbContentRatings
{
    public List<TmdbContentRating> Results { get; set; } = [];
}

public class TmdbContentRating
{
    [JsonPropertyName("iso_3166_1")] public string Iso31661 { get; set; } = "";
    public string Rating { get; set; } = "";
}

public class TmdbReleaseDates
{
    public List<TmdbReleaseCountry> Results { get; set; } = [];
}

public class TmdbReleaseCountry
{
    [JsonPropertyName("iso_3166_1")]     public string Iso31661 { get; set; } = "";
    [JsonPropertyName("release_dates")]  public List<TmdbReleaseDate> ReleaseDates { get; set; } = [];
}

public class TmdbReleaseDate
{
    public string Certification { get; set; } = "";
}

public class TmdbFindResponse
{
    [JsonPropertyName("movie_results")] public List<TmdbSearchResult> MovieResults { get; set; } = [];
    [JsonPropertyName("tv_results")]    public List<TmdbSearchResult> TvResults    { get; set; } = [];
}

public class TmdbCompany
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("logo_path")]    public string? LogoPath { get; set; }
    [JsonPropertyName("origin_country")] public string? OriginCountry { get; set; }
}

public class TmdbCrewMember
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; set; }
}

public class TmdbCredits
{
    public List<TmdbCastMember> Cast { get; set; } = [];
    public List<TmdbCrewEntry> Crew  { get; set; } = [];
}

public class TmdbCastMember
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Character { get; set; }
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; set; }
    public int Order { get; set; }
}

public class TmdbCrewEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Job { get; set; }
    public string? Department { get; set; }
}

public class TmdbKeywords
{
    /// <summary>Movie endpoint returns "keywords"; TV endpoint returns "results". Map both.</summary>
    [JsonPropertyName("results")]  public List<TmdbKeyword> Results { get; set; } = [];
    [JsonPropertyName("keywords")] public List<TmdbKeyword> Keywords { get; set; } = [];

    public IEnumerable<TmdbKeyword> All => Results.Count > 0 ? Results : Keywords;
}

public class TmdbKeyword
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TmdbTranslations
{
    public int Id { get; set; }
    public List<TmdbTranslation> Translations { get; set; } = [];
}

public class TmdbTranslation
{
    [JsonPropertyName("iso_3166_1")] public string Country { get; set; } = "";
    [JsonPropertyName("iso_639_1")]  public string Language { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("english_name")] public string EnglishName { get; set; } = "";
    public TmdbTranslationData? Data { get; set; }
}

public class TmdbTranslationData
{
    public string? Title { get; set; }      // movie endpoint
    public string? Name { get; set; }       // tv endpoint
    public string? Overview { get; set; }
    public string? Homepage { get; set; }
    public string? Tagline { get; set; }

    public string? DisplayTitle => Title ?? Name;
}
