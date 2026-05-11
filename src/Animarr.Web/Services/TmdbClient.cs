using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Lightweight TMDB API v3 client.
/// All image paths returned by TMDB are relative (e.g. "/abc123.jpg"); use BuildImageUrl() to build full URLs.
/// </summary>
public class TmdbClient(IHttpClientFactory httpFactory, ILogger<TmdbClient> logger)
{
    private const string BaseUrl   = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/{endpoint}?query={Uri.EscapeDataString(query)}&include_adult=false&language=en-US";
            var resp = await http.GetFromJsonAsync<TmdbPagedResponse<TmdbSearchResult>>(url, _json, ct);
            return resp?.Results ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB search failed for '{Query}' on {Endpoint}", query, endpoint);
            return [];
        }
    }

    // ── TV series detail ─────────────────────────────────────────────────────

    public async Task<TmdbTvDetail?> GetTvDetailAsync(int tmdbId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/tv/{tmdbId}?append_to_response=images,content_ratings,external_ids&language=en-US&include_image_language=en,null";
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"TMDB API key is invalid or not configured (HTTP {(int)resp.StatusCode}).");
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("TMDB GetTvDetail({Id}) returned HTTP {Status}", tmdbId, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<TmdbTvDetail>(_json, ct);
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB GetTvDetail failed for id={Id}", tmdbId);
            return null;
        }
    }

    public async Task<TmdbSeasonDetail?> GetSeasonDetailAsync(int tmdbId, int seasonNumber, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/tv/{tmdbId}/season/{seasonNumber}?append_to_response=images&language=en-US&include_image_language=en,null";
            return await http.GetFromJsonAsync<TmdbSeasonDetail>(url, _json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB GetSeason failed for id={Id} s{Season}", tmdbId, seasonNumber);
            return null;
        }
    }

    // ── Movie detail ─────────────────────────────────────────────────────────

    public async Task<TmdbMovieDetail?> GetMovieDetailAsync(int tmdbId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/movie/{tmdbId}?append_to_response=images,release_dates,external_ids&language=en-US&include_image_language=en,null";
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"TMDB API key is invalid or not configured (HTTP {(int)resp.StatusCode}).");
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("TMDB GetMovieDetail({Id}) returned HTTP {Status}", tmdbId, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<TmdbMovieDetail>(_json, ct);
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB GetMovieDetail failed for id={Id}", tmdbId);
            return null;
        }
    }

    // ── Find by external ID ──────────────────────────────────────────────────

    /// <summary>Find TMDB entries by external ID (IMDb or TVDB).</summary>
    /// <param name="externalId">e.g. "tt1234567" for IMDb, "83268" for TVDB</param>
    /// <param name="source">"imdb_id" or "tvdb_id"</param>
    public async Task<TmdbFindResponse?> FindByExternalIdAsync(string externalId, string source, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/find/{Uri.EscapeDataString(externalId)}?external_source={source}&language=en-US";
            return await http.GetFromJsonAsync<TmdbFindResponse>(url, _json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB FindByExternalId failed: id={Id} source={Source}", externalId, source);
            return null;
        }
    }

    // ── All images for a given TMDB entity ───────────────────────────────────

    /// <summary>Returns all posters, backdrops and logos for a TV series.</summary>
    public async Task<TmdbImages?> GetTvImagesAsync(int tmdbId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/tv/{tmdbId}/images?include_image_language=en,ja,ru,null";
            return await http.GetFromJsonAsync<TmdbImages>(url, _json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB GetTvImages failed for id={Id}", tmdbId);
            return null;
        }
    }

    /// <summary>Returns all posters, backdrops and logos for a movie.</summary>
    public async Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient("tmdb");
            var url = $"{BaseUrl}/movie/{tmdbId}/images?include_image_language=en,ja,ru,null";
            return await http.GetFromJsonAsync<TmdbImages>(url, _json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TMDB GetMovieImages failed for id={Id}", tmdbId);
            return null;
        }
    }

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
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("poster_path")]   public string? PosterPath   { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    public string? Overview { get; set; }
    public string? Tagline  { get; set; }
    public string? Status   { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")]   public int    VoteCount   { get; set; }
    [JsonPropertyName("episode_run_time")] public List<int> EpisodeRunTime { get; set; } = [];
    public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("number_of_seasons")] public int NumberOfSeasons { get; set; }
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummary> Seasons { get; set; } = [];
    [JsonPropertyName("external_ids")] public TmdbExternalIds? ExternalIds { get; set; }
    public TmdbImages? Images { get; set; }
    [JsonPropertyName("content_ratings")] public TmdbContentRatings? ContentRatings { get; set; }

    public int? Year => int.TryParse((FirstAirDate ?? "").Split('-')[0], out var y) ? y : null;
    public string? ContentRating => ContentRatings?.Results?.FirstOrDefault(r => r.Iso31661 == "US")?.Rating;
    public string? BestLogoPath  => Images?.Logos?.FirstOrDefault(i => i.Iso6391 == "en")?.FilePath
                                 ?? Images?.Logos?.FirstOrDefault()?.FilePath;
    public string? BestFanartPath => BackdropPath
                                  ?? Images?.Backdrops?.OrderByDescending(b => b.VoteAverage).FirstOrDefault()?.FilePath;
}

public class TmdbMovieDetail
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("release_date")]   public string? ReleaseDate   { get; set; }
    [JsonPropertyName("poster_path")]    public string? PosterPath    { get; set; }
    [JsonPropertyName("backdrop_path")]  public string? BackdropPath  { get; set; }
    public string? Overview  { get; set; }
    public string? Tagline   { get; set; }
    public string? Status    { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")]   public int    VoteCount   { get; set; }
    public int? Runtime { get; set; }
    public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("external_ids")] public TmdbExternalIds? ExternalIds { get; set; }
    public TmdbImages? Images { get; set; }
    [JsonPropertyName("release_dates")] public TmdbReleaseDates? ReleaseDates { get; set; }

    public int? Year => int.TryParse((ReleaseDate ?? "").Split('-')[0], out var y) ? y : null;
    public string? ContentRating => ReleaseDates?.Results?.FirstOrDefault(r => r.Iso31661 == "US")
        ?.ReleaseDates?.FirstOrDefault(r => r.Certification != "")?.Certification;
    public string? BestLogoPath   => Images?.Logos?.FirstOrDefault(i => i.Iso6391 == "en")?.FilePath
                                  ?? Images?.Logos?.FirstOrDefault()?.FilePath;
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
