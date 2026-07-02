using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

// Image picker (available posters/backdrops/logos), download and cross-source fallback.
public partial class MetadataService
{
    /// <summary>Returns all available poster/backdrop/logo candidates for the
    /// item. Each row carries the URL plus pixel width/height when the source
    /// reports them (TMDB does; MAL doesn't — those rows ship with 0/0 and
    /// the UI hides the dimension badge). Requires TmdbId or
    /// cross-referenceable ImdbId/TvdbId for the TMDB rows.</summary>
    public async Task<(List<ImageCandidateDto> Posters, List<ImageCandidateDto> Backdrops, List<ImageCandidateDto> Logos)>
        GetAvailableImagesAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folderId, ct);
        if (item is null) return ([], [], []);

        // If TmdbId is missing but we have an external ID, try to resolve it on-the-fly
        if (!item.TmdbId.HasValue)
        {
            var lookups = new List<(string id, string source)>();
            if (!string.IsNullOrWhiteSpace(item.ImdbId))
                lookups.Add((item.ImdbId, "imdb_id"));
            if (item.TvdbId.HasValue)
                lookups.Add((item.TvdbId.Value.ToString(), "tvdb_id"));

            foreach (var (extId, extSrc) in lookups)
            {
                var found = await tmdb.FindByExternalIdAsync(extId, extSrc, ct);
                if (found is null) continue;
                if (found.TvResults.Count > 0)
                {
                    item.TmdbId = found.TvResults[0].Id;
                    item.MediaType = MediaItemType.Series;
                }
                else if (found.MovieResults.Count > 0)
                {
                    item.TmdbId = found.MovieResults[0].Id;
                    item.MediaType = MediaItemType.Movie;
                }
                if (item.TmdbId.HasValue)
                {
                    await db.SaveChangesAsync(ct); // cache TmdbId for next time
                    break;
                }
            }
        }

        var posters   = new List<ImageCandidateDto>();
        var backdrops = new List<ImageCandidateDto>();
        var logos     = new List<ImageCandidateDto>();

        // TMDB (multiple variants per image, vote-sorted)
        if (item.TmdbId.HasValue)
        {
            var isTv   = item.MediaType != MediaItemType.Movie;
            var images = isTv
                ? await tmdb.GetTvImagesAsync(item.TmdbId.Value, ct)
                : await tmdb.GetMovieImagesAsync(item.TmdbId.Value, ct);

            if (images is not null)
            {
                static IEnumerable<ImageCandidateDto> ToCandidates(List<TmdbImage> list, Func<string, string> urlFn)
                    => list
                        .OrderByDescending(i => i.VoteAverage)
                        .Select(i => new ImageCandidateDto(urlFn(i.FilePath), i.Width, i.Height));

                posters  .AddRange(ToCandidates(images.Posters,   p => TmdbClient.PosterUrl(p,   "w342")));
                backdrops.AddRange(ToCandidates(images.Backdrops, p => TmdbClient.BackdropUrl(p, "w780")));
                logos    .AddRange(ToCandidates(images.Logos,     p => TmdbClient.LogoUrl(p,     "w300")));
            }
        }

        // MAL (anime) — append any extra poster candidates from the pictures
        // array. MAL doesn't report image dimensions in its API, so the
        // candidates ship with 0/0 and the UI hides the badge for them.
        if (item.MalId.HasValue)
        {
            var malDetail = await mal.GetDetailAsync(item.MalId.Value, ct);
            if (malDetail is not null)
            {
                IEnumerable<string?> malPosters = malDetail.Pictures
                    .Select(p => p.Large ?? p.Medium)
                    .Prepend(malDetail.MainPicture?.Large ?? malDetail.MainPicture?.Medium);
                foreach (var url in malPosters)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    if (posters.Any(p => p.Url == url)) continue;
                    posters.Add(new ImageCandidateDto(url, 0, 0));
                }
            }
        }

        return (posters, backdrops, logos);
    }

    /// <summary>Downloads the chosen image and saves it as poster/fanart/logo for the folder.</summary>
    public async Task ApplySelectedImageAsync(
        Guid folderId, string imageType, string imageUrl, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folder = await db.FolderWatchers.FindAsync([folderId], ct);
        var item   = await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folderId, ct);
        if (folder is null || item is null) return;

        var ext  = Path.GetExtension(imageUrl.Split('?')[0]);
        string fileName = imageType switch
        {
            "poster"  => "poster"  + (string.IsNullOrEmpty(ext) ? ".jpg" : ext),
            "fanart"  => "fanart"  + (string.IsNullOrEmpty(ext) ? ".jpg" : ext),
            "logo"    => "logo"    + (string.IsNullOrEmpty(ext) ? ".png" : ext),
            _ => throw new ArgumentException($"Unknown imageType: {imageType}")
        };

        // Use full-res URL: swap preview size for full
        var fullUrl = imageUrl
            .Replace("/w342/", "/original/")
            .Replace("/w780/", "/original/")
            .Replace("/w300/", "/original/");

        var metaDir  = MetaDir(folder);
        var destPath = Path.Combine(metaDir, fileName);
        if (!await tmdb.DownloadImageAsync(fullUrl, destPath, ct))
            throw new InvalidOperationException($"Failed to download image from {fullUrl}");

        // Store the absolute cache path — readers use Path.Combine(folder.Path, …)
        // which keeps the absolute path verbatim (Path.Combine drops the left side
        // when the right side is rooted). Backward-compatible with the old
        // ".animarr/poster.jpg"-style relative paths still sitting in the db.
        switch (imageType)
        {
            case "poster": item.PosterPath = destPath; break;
            case "fanart": item.FanartPath = destPath; break;
            case "logo":   item.LogoPath   = destPath; break;
        }
        item.LastMetadataRefreshedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }


    // ── Image download ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cache directory for this folder's posters, fanart, logos.
    /// Always lives inside <see cref="MediaCachePaths.CacheRoot"/> — never
    /// inside the user's media tree. SingleFilePath vs directory entries no
    /// longer need different layouts because each FolderWatcher has its own
    /// unique cache subdir keyed by Id.
    /// </summary>
    private string MetaDir(FolderWatcher folder) => cachePaths.ForFolder(folder.Id);

    private async Task DownloadImagesAsync(
        MediaItem item,
        FolderWatcher folder,
        string? poster,
        string? fanart,
        string? logo,
        bool forceRefresh,
        Action<string>? log,
        CancellationToken ct)
    {
        var metaDir = MetaDir(folder);

        if (poster != null)
        {
            var ext  = Path.GetExtension(poster.Split('?')[0]);
            var name = "poster" + (string.IsNullOrEmpty(ext) ? ".jpg" : ext);
            var dest = Path.Combine(metaDir, name);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading poster → {dest}");
                if (await tmdb.DownloadImageAsync(poster, dest, ct))
                { item.PosterPath = dest; log?.Invoke($"[Images] {dest} ✓"); }
                else
                { log?.Invoke($"[Images] {dest} ✗ (download failed)"); }
            }
            else
            {
                item.PosterPath = dest;
                log?.Invoke($"[Images] {dest} already exists, skipping");
            }
        }
        else
        {
            log?.Invoke("[Images] No poster URL.");
        }

        if (fanart != null)
        {
            var ext  = Path.GetExtension(fanart.Split('?')[0]);
            var name = "fanart" + (string.IsNullOrEmpty(ext) ? ".jpg" : ext);
            var dest = Path.Combine(metaDir, name);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading fanart → {dest}");
                if (await tmdb.DownloadImageAsync(fanart, dest, ct))
                { item.FanartPath = dest; log?.Invoke($"[Images] {dest} ✓"); }
                else
                { log?.Invoke($"[Images] {dest} ✗ (download failed)"); }
            }
            else
            {
                item.FanartPath = dest;
                log?.Invoke($"[Images] {dest} already exists, skipping");
            }
        }

        if (logo != null)
        {
            var ext  = Path.GetExtension(logo.Split('?')[0]);
            var name = "logo" + (string.IsNullOrEmpty(ext) ? ".png" : ext);
            var dest = Path.Combine(metaDir, name);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading logo → {dest}");
                if (await tmdb.DownloadImageAsync(logo, dest, ct))
                { item.LogoPath = dest; log?.Invoke($"[Images] {dest} ✓"); }
                else
                { log?.Invoke($"[Images] {dest} ✗ (download failed)"); }
            }
            else
            {
                item.LogoPath = dest;
                log?.Invoke($"[Images] {dest} already exists, skipping");
            }
        }
    }

    // ── Image fallback: fill missing images from other sources ───────────────

    /// <summary>
    /// After a primary populate, tries to fill any still-missing images (poster / fanart / logo)
    /// by querying additional sources in priority order:
    ///   1. TMDB via stored ImdbId  (FindByExternalId)
    ///   2. TMDB via stored TvdbId  (FindByExternalId)
    ///   3. MAL poster              (when item.MalId is set and poster still missing)
    ///
    /// If the primary source was already TMDB (item.TmdbId is set), TMDB steps are skipped.
    /// </summary>
    private async Task FillMissingImagesAsync(
        MediaItem item, FolderWatcher folder,
        bool forceRefresh, Action<string>? log, CancellationToken ct)
    {
        bool needPoster = item.PosterPath is null;
        bool needFanart = item.FanartPath is null;
        bool needLogo   = item.LogoPath   is null;
        if (!needPoster && !needFanart && !needLogo) return;

        var missing = string.Join(", ",
            new[] { needPoster ? "poster" : null, needFanart ? "fanart" : null, needLogo ? "logo" : null }
            .Where(x => x is not null));
        log?.Invoke($"[Images/Fallback] Missing after primary: {missing} — trying supplementary sources.");

        // ── 1 & 2: TMDB via external ID cross-ref (skipped if TMDB was primary) ─
        if (!item.TmdbId.HasValue)
        {
            // Build a list of (externalId, source) pairs to try
            var externalLookups = new List<(string id, string source)>();
            if (!string.IsNullOrWhiteSpace(item.ImdbId))
                externalLookups.Add((item.ImdbId, "imdb_id"));
            if (item.TvdbId.HasValue)
                externalLookups.Add((item.TvdbId.Value.ToString(), "tvdb_id"));

            foreach (var (extId, extSource) in externalLookups)
            {
                if (!needPoster && !needFanart && !needLogo) break;

                log?.Invoke($"[Images/Fallback] TMDB FindByExternalId({extId}, {extSource})");
                var find = await tmdb.FindByExternalIdAsync(extId, extSource, ct);
                if (find is null) continue;

                int? tmdbId = null;
                bool isTv   = false;
                if (find.TvResults.Count > 0)        { tmdbId = find.TvResults[0].Id;    isTv = true; }
                else if (find.MovieResults.Count > 0) { tmdbId = find.MovieResults[0].Id; }
                if (tmdbId is null) continue;

                item.TmdbId = tmdbId; // cache for future refreshes

                string? posterUrl = null, fanartUrl = null, logoUrl = null;
                if (isTv)
                {
                    var d = await tmdb.GetTvDetailAsync(tmdbId.Value, ct: ct);
                    if (d is not null)
                    {
                        if (needPoster && d.PosterPath     != null) posterUrl = TmdbClient.PosterUrl(d.PosterPath);
                        if (needFanart && d.BestFanartPath != null) fanartUrl = TmdbClient.BackdropUrl(d.BestFanartPath);
                        if (needLogo   && d.BestLogoPath   != null) logoUrl   = TmdbClient.LogoUrl(d.BestLogoPath);
                    }
                }
                else
                {
                    var d = await tmdb.GetMovieDetailAsync(tmdbId.Value, ct: ct);
                    if (d is not null)
                    {
                        if (needPoster && d.PosterPath   != null) posterUrl = TmdbClient.PosterUrl(d.PosterPath);
                        if (needFanart && d.BackdropPath != null) fanartUrl = TmdbClient.BackdropUrl(d.BackdropPath);
                        if (needLogo   && d.BestLogoPath != null) logoUrl   = TmdbClient.LogoUrl(d.BestLogoPath);
                    }
                }

                if (posterUrl is not null || fanartUrl is not null || logoUrl is not null)
                    await DownloadImagesAsync(item, folder, posterUrl, fanartUrl, logoUrl, forceRefresh, log, ct);

                needPoster = item.PosterPath is null;
                needFanart = item.FanartPath is null;
                needLogo   = item.LogoPath   is null;
                if (!needPoster && !needFanart && !needLogo) break;
            }
        }

        // ── 3. MAL poster (when poster still missing and MalId is known) ─────
        if (needPoster && item.MalId.HasValue)
        {
            log?.Invoke($"[Images/Fallback] MAL id={item.MalId} for poster");
            var detail = await mal.GetDetailAsync(item.MalId.Value, ct);
            if (detail?.PosterUrl is { Length: > 0 } posterUrl)
            {
                var metaDir  = MetaDir(folder);
                var destPath = Path.Combine(metaDir, "poster.jpg");
                if (forceRefresh || !File.Exists(destPath))
                {
                    if (await tmdb.DownloadImageAsync(posterUrl, destPath, ct))
                    { item.PosterPath = destPath; log?.Invoke($"[Images/Fallback] {destPath} from MAL ✓"); }
                    else
                    { log?.Invoke($"[Images/Fallback] {destPath} from MAL ✗"); }
                }
                else
                {
                    item.PosterPath = destPath;
                }
            }
        }
    }

}
