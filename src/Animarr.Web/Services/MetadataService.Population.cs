using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

// Populate MediaItem fields from the winning source (TMDB TV/Movie, MAL, IMDb) + enrichment.
public partial class MetadataService
{
    // ── TMDB populate ─────────────────────────────────────────────────────────

    private async Task<bool> PopulateTvFromTmdbAsync(
        MediaItem item, FolderWatcher folder, int tmdbId, bool forceRefresh,
        Action<string>? log, CancellationToken ct)
    {
        var lang = await appConfig.GetAsync(AppConfigKeys.MetadataLanguage, ct) ?? "en";
        var detail = await tmdb.GetTvDetailAsync(tmdbId, lang, ct);
        if (detail is null) { log?.Invoke($"[TMDB] GetTvDetail({tmdbId}) returned null."); return false; }

        log?.Invoke($"[TMDB] TV detail: \"{detail.Name}\" ({detail.Year})  seasons={detail.Seasons.Count}  lang={lang}");

        // Per-field English fallback: TMDB returns an empty overview (and occasionally
        // name) when the requested language has no translation — fill those from en-US.
        string? locName = detail.Name, locOverview = detail.Overview;
        if (lang != "en" && string.IsNullOrWhiteSpace(locOverview))
        {
            var en = await tmdb.GetTvDetailAsync(tmdbId, "en", ct);
            if (string.IsNullOrWhiteSpace(locOverview)) locOverview = en?.Overview;
            if (string.IsNullOrWhiteSpace(locName))     locName     = en?.Name;
        }

        item.TmdbId        = detail.Id;
        item.ImdbId        = detail.ExternalIds?.ImdbId;
        item.TvdbId        = detail.ExternalIds?.TvdbId;
        item.Title         = string.IsNullOrWhiteSpace(locName) ? detail.Name : locName;
        item.OriginalTitle = detail.OriginalName;
        item.Year          = detail.Year;
        item.Description   = locOverview;
        item.Tagline       = detail.Tagline;
        item.Status        = detail.Status;
        item.ContentRating = detail.ContentRating;
        item.Rating        = detail.VoteAverage > 0 ? detail.VoteAverage : null;
        item.RatingCount   = detail.VoteCount > 0 ? detail.VoteCount : null;
        item.Popularity    = detail.Popularity > 0 ? detail.Popularity : null;
        item.Runtime       = detail.EpisodeRunTime.FirstOrDefault();
        item.GenresJson    = JsonSerializer.Serialize(TmdbGenreCatalog.English(detail.Genres), _json);
        item.GenresLocalizedJson = lang != "en"
            ? JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json)
            : null;
        item.Studio        = detail.StudioName;
        item.Language      = LanguageNameMap.FromIso639(detail.OriginalLanguage);
        item.EpisodeCount  = detail.NumberOfEpisodes > 0
            ? detail.NumberOfEpisodes
            : detail.Seasons.Where(s => s.SeasonNumber > 0).Sum(s => s.EpisodeCount);
        item.SeasonLabel   = detail.NumberOfSeasons > 1
            ? $"S{detail.NumberOfSeasons}"
            : null;
        item.MediaType     = MediaItemType.Series;
        item.Hue          ??= HueHash.For(detail.Name);

        // Per-source confidence — TMDB has solid vote_count signal; map to 0..1.
        // (VoteCount of 500+ pegs at 1.0; 0 votes → 0.0 — keeps the curve readable in UI.)
        item.TmdbConfidence = Math.Min(1.0, detail.VoteCount / 500.0);

        // Descriptive tags from keywords (Donghua/Cultivation/Mecha-style labels).
        // Stored separately from genres because the design hero uses "tags pills" rather than genre tags.
        var keywords = detail.Keywords?.All.Select(k => k.Name).Take(8).ToList() ?? [];
        if (keywords.Count > 0)
            item.TagsJson = JsonSerializer.Serialize(keywords, _json);

        // CJK title — if the original language is a CJK locale, mirror OriginalName into CjkTitle
        // so the hero CJK watermark has a value separate from English-aliased OriginalTitle.
        if (detail.OriginalLanguage is "zh" or "ja" or "ko" && !string.IsNullOrWhiteSpace(detail.OriginalName))
            item.CjkTitle = detail.OriginalName;

        // English alternative — fetch translations and pick the en-US "name" if it differs from Title.
        await TryEnrichEnglishTitleAsync(item, isTv: true, detail.Id, ct);

        // Seasons — include PosterPath so Explorer can show thumbnails
        var seasons = detail.Seasons
            .Where(s => s.SeasonNumber > 0)
            .Select(s => new SeasonMeta
            {
                Number       = s.SeasonNumber,
                EpisodeCount = s.EpisodeCount,
                Name         = s.Name,
                PosterPath   = s.PosterPath != null
                    ? Path.Combine(MetaDir(folder), $"season{s.SeasonNumber}-poster.jpg")
                    : null,
            }).ToList();
        item.SeasonsJson = JsonSerializer.Serialize(seasons, _json);

        item.IdentificationStatus = IdentificationStatus.Identified;

        // Main images — prefer a poster localized to the metadata language.
        await DownloadImagesAsync(item, folder,
            poster:       detail.PickPosterPath(lang) is { } pp ? TmdbClient.PosterUrl(pp)                 : null,
            fanart:       detail.BestFanartPath != null ? TmdbClient.BackdropUrl(detail.BestFanartPath)   : null,
            logo:         detail.BestLogoPath   != null ? TmdbClient.LogoUrl(detail.BestLogoPath)         : null,
            forceRefresh: forceRefresh, log: log, ct: ct);

        // Season posters → <cache>/<folder-id>/seasonN-poster.jpg
        foreach (var s in detail.Seasons.Where(s => s.SeasonNumber > 0 && s.PosterPath != null))
        {
            var dest = Path.Combine(MetaDir(folder), $"season{s.SeasonNumber}-poster.jpg");
            if (!forceRefresh && File.Exists(dest)) continue;
            log?.Invoke($"[Images] Season {s.SeasonNumber} poster");
            await tmdb.DownloadImageAsync(TmdbClient.PosterUrl(s.PosterPath!), dest, ct);
        }

        return true;
    }

    private async Task<bool> PopulateMovieFromTmdbAsync(
        MediaItem item, FolderWatcher folder, int tmdbId, bool forceRefresh,
        Action<string>? log, CancellationToken ct)
    {
        var lang = await appConfig.GetAsync(AppConfigKeys.MetadataLanguage, ct) ?? "en";
        var detail = await tmdb.GetMovieDetailAsync(tmdbId, lang, ct);
        if (detail is null) { log?.Invoke($"[TMDB] GetMovieDetail({tmdbId}) returned null."); return false; }

        log?.Invoke($"[TMDB] Movie detail: \"{detail.Title}\" ({detail.Year})  lang={lang}");

        // Per-field English fallback for an untranslated overview/title.
        string? locTitle = detail.Title, locOverview = detail.Overview;
        if (lang != "en" && string.IsNullOrWhiteSpace(locOverview))
        {
            var en = await tmdb.GetMovieDetailAsync(tmdbId, "en", ct);
            if (string.IsNullOrWhiteSpace(locOverview)) locOverview = en?.Overview;
            if (string.IsNullOrWhiteSpace(locTitle))    locTitle    = en?.Title;
        }

        item.TmdbId        = detail.Id;
        item.ImdbId        = detail.ExternalIds?.ImdbId;
        item.TvdbId        = detail.ExternalIds?.TvdbId;
        item.Title         = string.IsNullOrWhiteSpace(locTitle) ? detail.Title : locTitle;
        item.OriginalTitle = detail.OriginalTitle;
        item.Year          = detail.Year;
        item.Description   = locOverview;
        item.Tagline       = detail.Tagline;
        item.Status        = detail.Status;
        item.ContentRating = detail.ContentRating;
        item.Rating        = detail.VoteAverage > 0 ? detail.VoteAverage : null;
        item.RatingCount   = detail.VoteCount > 0 ? detail.VoteCount : null;
        item.Popularity    = detail.Popularity > 0 ? detail.Popularity : null;
        item.Runtime       = detail.Runtime;
        item.GenresJson    = JsonSerializer.Serialize(TmdbGenreCatalog.English(detail.Genres), _json);
        item.GenresLocalizedJson = lang != "en"
            ? JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json)
            : null;
        item.Studio        = detail.StudioName;
        item.Language      = LanguageNameMap.FromIso639(detail.OriginalLanguage);
        item.EpisodeCount  = null;          // movies have no episodes — explicit null beats stale value on re-identify
        item.SeasonLabel   = null;
        item.MediaType     = MediaItemType.Movie;
        item.Hue          ??= HueHash.For(detail.Title);
        item.TmdbConfidence = Math.Min(1.0, detail.VoteCount / 500.0);

        var keywords = detail.Keywords?.All.Select(k => k.Name).Take(8).ToList() ?? [];
        if (keywords.Count > 0)
            item.TagsJson = JsonSerializer.Serialize(keywords, _json);

        if (detail.OriginalLanguage is "zh" or "ja" or "ko" && !string.IsNullOrWhiteSpace(detail.OriginalTitle))
            item.CjkTitle = detail.OriginalTitle;

        await TryEnrichEnglishTitleAsync(item, isTv: false, detail.Id, ct);

        item.IdentificationStatus = IdentificationStatus.Identified;

        await DownloadImagesAsync(item, folder,
            poster:       detail.PickPosterPath(lang) is { } pp ? TmdbClient.PosterUrl(pp)           : null,
            fanart:       detail.BackdropPath != null ? TmdbClient.BackdropUrl(detail.BackdropPath) : null,
            logo:         detail.BestLogoPath != null ? TmdbClient.LogoUrl(detail.BestLogoPath)    : null,
            forceRefresh: forceRefresh, log: log, ct: ct);

        return true;
    }

    // ── MAL full populate (winner = MAL) ──────────────────────────────────────

    private async Task<bool> PopulateFromMalAsync(
        MediaItem item, FolderWatcher folder, int malId, Action<string>? log, CancellationToken ct)
    {
        var detail = await mal.GetDetailAsync(malId, ct);
        if (detail is null) { log?.Invoke($"[MAL] GetDetail({malId}) returned null."); return false; }

        log?.Invoke($"[MAL] Detail: \"{detail.EnglishTitle ?? detail.Title}\" id={detail.Id}");

        item.MalId         = detail.Id;
        item.Title         = detail.EnglishTitle ?? detail.Title;
        item.OriginalTitle = detail.AlternativeTitles?.Ja ?? detail.Title;
        // MAL anime are virtually always Japanese — populate CjkTitle when we have a JA alt-title
        // distinct from the romanized display title.
        if (!string.IsNullOrWhiteSpace(detail.AlternativeTitles?.Ja)
            && detail.AlternativeTitles.Ja != item.Title)
            item.CjkTitle = detail.AlternativeTitles.Ja;
        if (!string.IsNullOrWhiteSpace(detail.AlternativeTitles?.En)
            && detail.AlternativeTitles.En != item.Title)
            item.EnglishTitle = detail.AlternativeTitles.En;
        item.Year          ??= detail.Year;
        item.Description   ??= detail.Synopsis;
        if (item.Rating is null && detail.Mean.HasValue)               item.Rating      = detail.Mean;
        if (item.RatingCount is null && detail.NumScoringUsers.HasValue) item.RatingCount = detail.NumScoringUsers;
        if (item.Popularity is null && detail.Popularity.HasValue)     item.Popularity  = detail.Popularity;
        if (item.Studio is null && detail.StudioName is not null)      item.Studio      = detail.StudioName;
        if (item.Runtime is null && detail.RuntimeMinutes.HasValue)    item.Runtime     = detail.RuntimeMinutes;
        // MAL anime are Japanese by default — only set if not already set by a higher-priority TMDB pass.
        item.Language     ??= "Japanese";
        if (detail.NumEpisodes.HasValue && detail.NumEpisodes > 0)
            item.EpisodeCount = detail.NumEpisodes;
        item.SeasonLabel  ??= detail.StartSeason is not null
            ? $"{Capitalize(detail.StartSeason.Season)} {detail.StartSeason.Year}"
            : null;
        item.Hue          ??= HueHash.For(item.Title);
        // MAL confidence proxy: num_scoring_users — 50k+ voters pegs at 1.0.
        item.MalConfidence = detail.NumScoringUsers.HasValue
            ? Math.Min(1.0, detail.NumScoringUsers.Value / 50000.0)
            : null;

        if (detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);

        item.MediaType            = MediaItemType.Anime;
        item.IdentificationStatus = IdentificationStatus.Identified;

        // MAL has no concept of seasons — the show is a single contiguous run.
        // Synthesise a Season 1 entry with NumEpisodes so MediaDetail renders
        // an episode list (each card marked ✓ or download based on file presence)
        // instead of an empty page.
        if (string.IsNullOrEmpty(item.SeasonsJson) && (detail.NumEpisodes ?? 0) > 0)
        {
            item.SeasonsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    Number       = 1,
                    EpisodeCount = detail.NumEpisodes!.Value,
                    Name         = "Season 1",
                    PosterPath   = (string?)null,
                    Overview     = (string?)null,
                    AirDate      = (string?)null,
                }
            }, _json);
        }

        if (item.PosterPath is null && detail.PosterUrl is not null)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            if (!File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading MAL poster → {destPath}");
                if (await tmdb.DownloadImageAsync(detail.PosterUrl, destPath, ct))
                { item.PosterPath = destPath; log?.Invoke($"[Images] {destPath} ✓"); }
                else
                { log?.Invoke($"[Images] {destPath} ✗ (download failed)"); }
            }
            else
            {
                item.PosterPath = destPath;
            }
        }

        return true;
    }

    // ── IMDb search → resolve via TMDB FindByExternalId, fallback to imdbapi.dev ──

    private async Task<bool> PopulateFromImdbSearchAsync(
        MediaItem item, FolderWatcher folder, string imdbId, bool preferTv,
        bool forceRefresh, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"[IMDb] Resolving {imdbId} via TMDB FindByExternalId");
        var findResult = await tmdb.FindByExternalIdAsync(imdbId, "imdb_id", ct);
        if (findResult is not null)
        {
            item.ImdbId = imdbId;
            if (preferTv && findResult.TvResults.Count > 0)
                return await PopulateTvFromTmdbAsync(item, folder, findResult.TvResults[0].Id, forceRefresh, log, ct);
            if (findResult.MovieResults.Count > 0)
                return await PopulateMovieFromTmdbAsync(item, folder, findResult.MovieResults[0].Id, forceRefresh, log, ct);
            if (findResult.TvResults.Count > 0)
                return await PopulateTvFromTmdbAsync(item, folder, findResult.TvResults[0].Id, forceRefresh, log, ct);
            log?.Invoke($"[IMDb] TMDB returned no TV or movie results for {imdbId}.");
        }
        else
        {
            log?.Invoke($"[IMDb] TMDB lookup for {imdbId} returned null — falling back to imdbapi.dev direct.");
        }

        // Fallback: populate directly from imdbapi.dev (no TMDB key required)
        return await PopulateFromImdbDirectAsync(item, folder, imdbId, forceRefresh, log, ct);
    }

    /// <summary>Populate MediaItem from imdbapi.dev /titles/{id} without requiring TMDB key.</summary>
    private async Task<bool> PopulateFromImdbDirectAsync(
        MediaItem item, FolderWatcher folder, string imdbId,
        bool forceRefresh, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"[IMDb] Fetching direct detail for {imdbId} from imdbapi.dev");
        var detail = await imdbSearch.GetTitleAsync(imdbId, ct);
        if (detail is null)
        {
            log?.Invoke($"[IMDb] Direct detail for {imdbId} returned null.");
            return false;
        }

        log?.Invoke($"[IMDb] Direct detail: \"{detail.PrimaryTitle}\" ({detail.StartYear}) type={detail.Type}");

        item.ImdbId        = imdbId;
        item.Title         = detail.PrimaryTitle;
        item.OriginalTitle = detail.OriginalTitle;
        item.Year          = detail.StartYear;
        item.Description   = detail.Plot;
        item.Runtime       = detail.RuntimeSeconds.HasValue ? detail.RuntimeSeconds.Value / 60 : null;
        if (detail.Rating is not null)
        {
            item.Rating      = detail.Rating.AggregateRating;
            item.RatingCount = detail.Rating.VoteCount;
            // IMDb confidence proxy: vote_count. IMDb's bar is higher than TMDB's because
            // it's the long-tail source — 10k voters → ~1.0; matches what "established title" feels like.
            item.ImdbConfidence = Math.Min(1.0, detail.Rating.VoteCount / 10000.0);
        }
        if (detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres, _json);

        bool isTv = detail.Type is "tvSeries" or "tvMiniSeries" or "tvSpecial";
        item.MediaType = isTv ? MediaItemType.Series : MediaItemType.Movie;
        item.Hue      ??= HueHash.For(detail.PrimaryTitle);
        item.IdentificationStatus = IdentificationStatus.Identified;

        // Download poster from imdbapi.dev primaryImage if available
        if (detail.PrimaryImage?.Url is { Length: > 0 } posterUrl)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            if (forceRefresh || item.PosterPath is null || !File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading IMDb poster → {destPath}");
                if (await tmdb.DownloadImageAsync(posterUrl, destPath, ct))
                { item.PosterPath = destPath; log?.Invoke($"[Images] {destPath} ✓"); }
                else
                { log?.Invoke($"[Images] {destPath} ✗ (download failed)"); }
            }
            else if (File.Exists(destPath))
            {
                item.PosterPath = destPath;
            }
        }

        return true;
    }

    // ── MAL enrichment (supplements existing TMDB data) ──────────────────────

    private async Task EnrichWithMalAsync(
        MediaItem item, FolderWatcher folder, int malId, bool forceRefresh,
        Action<string>? log, CancellationToken ct)
    {
        var detail = await mal.GetDetailAsync(malId, ct);
        if (detail is null) { log?.Invoke($"[MAL] GetDetail({malId}) returned null."); return; }

        log?.Invoke($"[MAL] Enriching: \"{detail.EnglishTitle ?? detail.Title}\" id={detail.Id}");

        item.MalId = detail.Id;
        if (string.IsNullOrWhiteSpace(item.Title))    item.Title         = detail.EnglishTitle;
        if (item.OriginalTitle is null)                item.OriginalTitle = detail.AlternativeTitles?.Ja ?? detail.Title;
        // Enrich CJK / English alts only when missing — TMDB pass already populated them when it ran first.
        if (item.CjkTitle is null && !string.IsNullOrWhiteSpace(detail.AlternativeTitles?.Ja))
            item.CjkTitle = detail.AlternativeTitles.Ja;
        if (item.EnglishTitle is null && !string.IsNullOrWhiteSpace(detail.AlternativeTitles?.En)
            && detail.AlternativeTitles.En != item.Title)
            item.EnglishTitle = detail.AlternativeTitles.En;
        if (item.Year is null)                         item.Year          = detail.Year;
        if (item.Description is null)                  item.Description   = detail.Synopsis;
        if (item.Rating is null && detail.Mean.HasValue)               item.Rating     = detail.Mean;
        if (item.RatingCount is null && detail.NumScoringUsers.HasValue) item.RatingCount = detail.NumScoringUsers;
        if (item.Popularity is null && detail.Popularity.HasValue)      item.Popularity = detail.Popularity;
        if (item.Studio is null && detail.StudioName is not null)       item.Studio     = detail.StudioName;
        if (item.Runtime is null && detail.RuntimeMinutes.HasValue)     item.Runtime    = detail.RuntimeMinutes;
        item.Language ??= "Japanese";
        if (item.EpisodeCount is null && detail.NumEpisodes.HasValue && detail.NumEpisodes > 0)
            item.EpisodeCount = detail.NumEpisodes;
        if (item.SeasonLabel is null && detail.StartSeason is not null)
            item.SeasonLabel = $"{Capitalize(detail.StartSeason.Season)} {detail.StartSeason.Year}";
        item.Hue ??= HueHash.For(item.Title);
        item.MalConfidence ??= detail.NumScoringUsers.HasValue
            ? Math.Min(1.0, detail.NumScoringUsers.Value / 50000.0)
            : null;
        if (item.GenresJson is null && detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);

        item.MediaType = MediaItemType.Anime;

        if (item.PosterPath is null && detail.PosterUrl is not null)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            if (forceRefresh || !File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading MAL poster → {destPath}");
                if (await tmdb.DownloadImageAsync(detail.PosterUrl, destPath, ct))
                { item.PosterPath = destPath; log?.Invoke($"[Images] {destPath} ✓"); }
            }
            else
            {
                item.PosterPath = destPath;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>Fetch translations and pick the en-US "name"/"title" when it differs from item.Title.
    /// Cheap (one extra GET) but only fires when the primary detail came back in a non-English locale.</summary>
    private async Task TryEnrichEnglishTitleAsync(MediaItem item, bool isTv, int tmdbId, CancellationToken ct)
    {
        // Skip when we already have a distinct English title or the primary title is already English.
        if (!string.IsNullOrWhiteSpace(item.EnglishTitle)) return;

        var translations = isTv
            ? await tmdb.GetTvTranslationsAsync(tmdbId, ct)
            : await tmdb.GetMovieTranslationsAsync(tmdbId, ct);
        if (translations is null) return;

        var en = translations.Translations
            .FirstOrDefault(t => t.Language == "en" && t.Country == "US")
            ?? translations.Translations.FirstOrDefault(t => t.Language == "en");

        var enTitle = en?.Data?.DisplayTitle;
        if (!string.IsNullOrWhiteSpace(enTitle) && enTitle != item.Title)
            item.EnglishTitle = enTitle;
    }

}
