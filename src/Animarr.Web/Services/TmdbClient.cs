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

    // ── Localisation helpers ─────────────────────────────────────────────────

    /// <summary>Map a UI language code (en/ru/uk/de/es) to a TMDB locale.
    /// Unknown/empty → en-US, preserving the historical default.</summary>
    public static string ToTmdbLocale(string? code) => code switch
    {
        "ru" => "ru-RU",
        "uk" => "uk-UA",
        "de" => "de-DE",
        "es" => "es-ES",
        _    => "en-US",
    };

    /// <summary>Build the <c>include_image_language</c> value: the selected
    /// language first (so its localized posters/logos are returned), then English
    /// as a fallback, the CJK originals, and language-neutral ("null") art.</summary>
    private static string ImageLangParam(string? code)
    {
        var lang  = string.IsNullOrEmpty(code) ? "en" : code;
        var parts = new List<string> { lang };
        foreach (var p in new[] { "en", "ja", "zh", "null" })
            if (!parts.Contains(p)) parts.Add(p);
        return string.Join(",", parts);
    }

    // ── TV series detail ─────────────────────────────────────────────────────

    /// <param name="language">UI language code (en/ru/uk/de/es). null → English.</param>
    public Task<TmdbTvDetail?> GetTvDetailAsync(int tmdbId, string? language = null, CancellationToken ct = default)
        => GetJsonAsync<TmdbTvDetail>($"{BaseUrl}/tv/{tmdbId}?append_to_response=images,content_ratings,external_ids,credits,keywords&language={ToTmdbLocale(language)}&include_image_language={ImageLangParam(language)}", ct);

    /// <param name="language">UI language code (en/ru/uk/de/es). null → English.
    /// Controls episode Name/Overview localization.</param>
    public Task<TmdbSeasonDetail?> GetSeasonDetailAsync(int tmdbId, int seasonNumber, string? language = null, CancellationToken ct = default)
        => GetJsonAsync<TmdbSeasonDetail>($"{BaseUrl}/tv/{tmdbId}/season/{seasonNumber}?append_to_response=images&language={ToTmdbLocale(language)}&include_image_language={ImageLangParam(language)}", ct);

    // ── Movie detail ─────────────────────────────────────────────────────────

    /// <param name="language">UI language code (en/ru/uk/de/es). null → English.</param>
    public Task<TmdbMovieDetail?> GetMovieDetailAsync(int tmdbId, string? language = null, CancellationToken ct = default)
        => GetJsonAsync<TmdbMovieDetail>($"{BaseUrl}/movie/{tmdbId}?append_to_response=images,release_dates,external_ids,credits,keywords&language={ToTmdbLocale(language)}&include_image_language={ImageLangParam(language)}", ct);

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

    // ── Related titles (similar + recommendations) ──────────────────────────

    /// <summary>TMDB's two relatedness feeds for one title, merged and deduped
    /// (recommendations first — they're behaviour-based and noticeably better
    /// than the tag-based /similar). This is the external backfill pool for the
    /// "More like this" / "For you" rails.</summary>
    public async Task<List<TmdbSearchResult>> GetRelatedAsync(int tmdbId, bool isMovie, CancellationToken ct = default)
    {
        var kind = isMovie ? "movie" : "tv";
        var rec = await GetJsonAsync<TmdbPagedResponse<TmdbSearchResult>>($"{BaseUrl}/{kind}/{tmdbId}/recommendations?language=en-US", ct);
        var sim = await GetJsonAsync<TmdbPagedResponse<TmdbSearchResult>>($"{BaseUrl}/{kind}/{tmdbId}/similar?language=en-US", ct);
        return (rec?.Results ?? []).Concat(sim?.Results ?? [])
            .Where(r => r.Id > 0 && !string.IsNullOrEmpty(r.DisplayTitle))
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();
    }

    // ── All images for a given TMDB entity ───────────────────────────────────

    /// <summary>Returns ALL posters, backdrops and logos for a TV series, in
    /// every language. No include_image_language / language filter — the manual
    /// poster/backdrop picker in Edit Metadata shows the full set so the user
    /// chooses whichever they like (TMDB returns the complete set when neither
    /// language param is sent).</summary>
    public Task<TmdbImages?> GetTvImagesAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbImages>($"{BaseUrl}/tv/{tmdbId}/images", ct);

    /// <summary>Returns ALL posters, backdrops and logos for a movie, in every
    /// language (no language filter — see <see cref="GetTvImagesAsync"/>).</summary>
    public Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, CancellationToken ct = default)
        => GetJsonAsync<TmdbImages>($"{BaseUrl}/movie/{tmdbId}/images", ct);

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

    /// <summary>Localized poster path: prefer one tagged with <paramref name="lang"/>,
    /// then TMDB's default poster (already language-aware), then any — so we never
    /// fall back to no poster when a translation lacks localized art.</summary>
    public string? PickPosterPath(string? lang) =>
        (string.IsNullOrEmpty(lang) ? null : Images?.Posters?.FirstOrDefault(p => p.Iso6391 == lang)?.FilePath)
        ?? PosterPath
        ?? Images?.Posters?.FirstOrDefault()?.FilePath;
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

    /// <summary>Localized poster path: prefer one tagged with <paramref name="lang"/>,
    /// then TMDB's default poster (already language-aware), then any — so we never
    /// fall back to no poster when a translation lacks localized art.</summary>
    public string? PickPosterPath(string? lang) =>
        (string.IsNullOrEmpty(lang) ? null : Images?.Posters?.FirstOrDefault(p => p.Iso6391 == lang)?.FilePath)
        ?? PosterPath
        ?? Images?.Posters?.FirstOrDefault()?.FilePath;
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

/// <summary>
/// TMDB's genre taxonomy is stable and id-keyed; the <c>name</c> in a detail
/// response is localized to the requested language. We persist genres canonically
/// in English because catalog logic — anime detection, category classification,
/// theme-music matching — keys off these exact names. This maps the stable id back
/// to its English label regardless of the language the detail was fetched in.
/// </summary>
public static class TmdbGenreCatalog
{
    // The full TMDB movie + TV genre list (ids are universal across both).
    private static readonly Dictionary<int, string> _en = new()
    {
        [28] = "Action",            [12] = "Adventure",          [16] = "Animation",
        [35] = "Comedy",            [80] = "Crime",              [99] = "Documentary",
        [18] = "Drama",             [10751] = "Family",          [14] = "Fantasy",
        [36] = "History",           [27] = "Horror",             [10402] = "Music",
        [9648] = "Mystery",         [10749] = "Romance",         [878] = "Science Fiction",
        [10770] = "TV Movie",       [53] = "Thriller",           [10752] = "War",
        [37] = "Western",           [10759] = "Action & Adventure", [10762] = "Kids",
        [10763] = "News",           [10764] = "Reality",         [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",           [10767] = "Talk",            [10768] = "War & Politics",
    };

    /// <summary>English label for a TMDB genre id, falling back to the (possibly
    /// localized) name when the id is unknown.</summary>
    public static string English(int id, string fallback)
        => _en.TryGetValue(id, out var name) ? name : fallback;

    /// <summary>Project a detail's genre list to canonical English names.</summary>
    public static List<string> English(IEnumerable<TmdbGenre> genres)
        => genres.Select(g => English(g.Id, g.Name)).ToList();
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
    /// <summary>Asset pixel width as reported by TMDB. 0 when missing.
    /// Surfaced on the Edit Metadata poster/backdrop picker as a badge so
    /// the user can compare candidates by resolution before picking.</summary>
    [JsonPropertyName("width")]        public int     Width       { get; set; }
    [JsonPropertyName("height")]       public int     Height      { get; set; }
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
