using System.Text.Json;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Orchestrates metadata lookup: TMDB (series/movies) + MAL (anime).
/// Searches all enabled sources in parallel, scores results, optionally uses LLM
/// for final selection, then downloads images.
/// Called by IdentificationQueueProcessorService after the optional LLM title-normalisation step.
/// </summary>
public class MetadataService(
    IDbContextFactory<AppDbContext> dbFactory,
    TmdbClient tmdb,
    MalClient mal,
    ImdbSearchClient imdbSearch,
    IAppConfigService appConfig,
    ILlmService llm,
    FolderWatcherService watcher,
    TorrentEngineService torrentEngine,
    ILogger<MetadataService> logger)
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    // Candidate gathered from any search source
    private sealed record MetadataCandidate(
        string  Source,          // "tmdb_tv" | "tmdb_movie" | "mal" | "imdb_search"
        int     Id,
        string  Title,
        string? OriginalTitle,
        int?    Year,
        string? Overview,
        bool    IsTv,
        double  Score,
        string? StringId = null);  // non-integer IDs (e.g. IMDb "tt...")

    // ── Public: automatic identification ─────────────────────────────────────

    public async Task IdentifyFolderAsync(
        Guid folderId,
        LlmIdentifyResult? llmResult,
        bool forceRefresh,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folder = await db.FolderWatchers.FindAsync([folderId], ct);
        if (folder is null) return;

        log?.Invoke($"[Queue] Folder: {folder.Path}");

        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folderId, ct)
                   ?? new MediaItem { Id = Guid.NewGuid(), FolderId = folderId, CreatedAt = DateTime.UtcNow };
        bool isNew = !await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);

        if (!forceRefresh && item.IdentificationStatus == IdentificationStatus.Identified)
        {
            log?.Invoke("[Queue] Already identified, skipping.");
            logger.LogDebug("Folder {Id} already identified, skipping.", folderId);
            return;
        }

        item.LlmIdentifiedTitle = llmResult?.Title;
        item.LlmConfidence      = llmResult?.Confidence;

        // Seed Title with a folder/file-name fallback so failed-to-identify items
        // still show something readable in the catalog and detail view. Populate*Async
        // overwrites this with the canonical title when identification succeeds.
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            item.Title = folder.SingleFilePath != null
                ? Path.GetFileNameWithoutExtension(folder.SingleFilePath)
                : Path.GetFileName(folder.Path.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        // Title/year source — flat files use the filename without extension; otherwise the folder path.
        var titleSource = folder.SingleFilePath != null
            ? Path.GetFileNameWithoutExtension(folder.SingleFilePath)
            : folder.Path;
        var searchTitle = llmResult?.Title ?? ParseTitleFromPath(titleSource);
        var folderYear  = llmResult?.Year ?? ExtractYearFromPath(titleSource);

        // Phase 1.5: prefer LLM type hint over manual FolderType when confidence is decent.
        // This biases TMDB endpoint selection (TV vs Movie) without forcing the user to
        // configure FolderType manually.
        var typeHint = folder.FolderType;
        if (llmResult is { Confidence: >= 0.7 })
        {
            typeHint = llmResult.Type switch
            {
                "movie"  => FolderType.Movie,
                "series" => FolderType.Series,
                "anime"  => FolderType.Series,  // anime → TV endpoint of TMDB + MAL
                _        => typeHint,
            };
        }

        // SingleFilePath entries are individual files in a flat section — by definition
        // they are MOVIES, not series. Override any "anime"/"series" hint to force the
        // Movie endpoint, otherwise franchise movies (Gundam SEED Freedom, Gundam 00
        // A Wakening, Renegade Immortal: Battle of Gods, Douluo Dalu: Sword Master)
        // get matched to the parent SERIES on TMDB and produce duplicate MediaItems.
        if (folder.SingleFilePath != null)
            typeHint = FolderType.Movie;

        log?.Invoke(llmResult != null
            ? $"[LLM] Title: \"{llmResult.Title}\" type={typeHint} year={folderYear} (confidence {llmResult.Confidence:F2})"
            : $"[Parse] Path title: \"{searchTitle}\" year={folderYear}");
        logger.LogInformation("Identifying folder '{Path}' with title '{Title}' type={Type}", folder.Path, searchTitle, typeHint);

        var tmdbKey = await appConfig.GetAsync(AppConfigKeys.TmdbApiKey, ct);
        var malKey  = await appConfig.GetAsync(AppConfigKeys.MalClientId, ct);

        // ── Shortcut: if a stored ImdbId exists on the item, use it directly ─
        if (!string.IsNullOrWhiteSpace(item.ImdbId))
        {
            log?.Invoke($"[IMDb] Re-identifying using stored ImdbId: {item.ImdbId}");
            bool directOk = await PopulateFromImdbSearchAsync(
                item, folder, item.ImdbId,
                preferTv: item.MediaType != MediaItemType.Movie,
                forceRefresh: forceRefresh, log: log, ct: ct);
            if (directOk)
            {
                item.LastMetadataRefreshedAt = DateTime.UtcNow;
                if (isNew) db.MediaItems.Add(item);
                await db.SaveChangesAsync(ct);
                return;
            }
            log?.Invoke("[IMDb] Stored ImdbId lookup failed — continuing with title search.");
        }

        // ── Phase 1: Gather all candidates in parallel ────────────────────────
        var candidates = await GatherCandidatesAsync(
            searchTitle, typeHint, tmdbKey, malKey, folderYear, log, ct);

        // Fallback A: if the LLM-normalised title returned nothing, try the raw
        // folder or file name. For SingleFilePath entries this is the filename
        // (without extension) — never the generic section dir like "Movies".
        var rawFolderName = folder.SingleFilePath != null
            ? Path.GetFileNameWithoutExtension(folder.SingleFilePath)
            : Path.GetFileName(folder.Path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (candidates.Count == 0 &&
            !string.IsNullOrWhiteSpace(rawFolderName) &&
            !string.Equals(rawFolderName, searchTitle, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[Search] Empty — retrying with raw folder name: \"{rawFolderName}\"");
            candidates = await GatherCandidatesAsync(
                rawFolderName, typeHint, tmdbKey, malKey, folderYear, log, ct);
        }

        // Fallback B: try the LLM's english_title hint — the canonical English
        // translation it provides when the source name is pinyin/romaji/Cyrillic.
        // This is the highest-yield fallback for Chinese donghua and similar.
        if (candidates.Count == 0 &&
            !string.IsNullOrWhiteSpace(llmResult?.EnglishTitle) &&
            !string.Equals(llmResult.EnglishTitle, searchTitle, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[Search] Empty — retrying with LLM english_title: \"{llmResult.EnglishTitle}\"");
            candidates = await GatherCandidatesAsync(
                llmResult.EnglishTitle, typeHint, tmdbKey, malKey, folderYear, log, ct);
        }

        // Fallback B2: try the LLM's original_title hint if it was provided.
        if (candidates.Count == 0 &&
            !string.IsNullOrWhiteSpace(llmResult?.OriginalTitle) &&
            !string.Equals(llmResult.OriginalTitle, searchTitle, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[Search] Empty — retrying with LLM original_title: \"{llmResult.OriginalTitle}\"");
            candidates = await GatherCandidatesAsync(
                llmResult.OriginalTitle, typeHint, tmdbKey, malKey, folderYear, log, ct);
        }

        // Fallback C: progressively shorter prefixes — for "Code Geass Dakkan no Rose"
        // try "Code Geass Dakkan no", "Code Geass Dakkan", then "Code Geass" — the
        // first 2-3 words are usually the franchise/series name that TMDB does know.
        if (candidates.Count == 0)
        {
            var words = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int take = words.Length - 1; take >= 2 && candidates.Count == 0; take--)
            {
                var prefix = string.Join(' ', words.Take(take));
                log?.Invoke($"[Search] Empty — retrying with prefix: \"{prefix}\"");
                candidates = await GatherCandidatesAsync(
                    prefix, typeHint, tmdbKey, malKey, folderYear, log, ct);
            }
        }

        if (candidates.Count == 0)
        {
            log?.Invoke("[Search] No candidates found from any source.");
            item.IdentificationStatus    = IdentificationStatus.Failed;
            item.LastMetadataRefreshedAt = DateTime.UtcNow;
            if (isNew) db.MediaItems.Add(item);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Save top-3 for the UI NeedsReview view
        item.CandidatesJson = JsonSerializer.Serialize(
            candidates.OrderByDescending(c => c.Score).Take(3).Select(c => new
            {
                Id       = c.Id,
                Title    = c.Title,
                Year     = c.Year,
                Type     = c.Source,
                Overview = c.Overview,
            }).ToList(), _json);

        // ── Phase 2: Pick winner (LLM or top-scorer) ─────────────────────────
        var winner = await SelectWinnerAsync(candidates, folder.Path, folderYear, log, ct);
        log?.Invoke($"[Winner] \"{winner.Title}\" ({winner.Year}) [{winner.Source}] id={winner.Id}  score={winner.Score:F2}");

        // Anti-duplicate: if another MediaItem already points at this exact
        // TMDB/MAL entry, this is almost certainly a misidentification
        // (e.g. a franchise movie matched against the parent series). Try the
        // NEXT candidate; if none, fall through to NeedsReview.
        var winnerExternalId = winner.Source == "imdb_search" ? null : (int?)winner.Id;
        if (winnerExternalId.HasValue)
        {
            var dup = await db.MediaItems.AnyAsync(m =>
                m.Id != item.Id &&
                ((winner.Source == "tmdb_tv" || winner.Source == "tmdb_movie") && m.TmdbId == winnerExternalId
                 || winner.Source == "mal" && m.MalId == winnerExternalId), ct);
            if (dup)
            {
                log?.Invoke($"[Winner] {winner.Source}#{winner.Id} is already used by another MediaItem — trying next candidate.");
                var alt = candidates
                    .Where(c => c != winner)
                    .OrderByDescending(c => c.Score)
                    .FirstOrDefault(c =>
                        winner.Source == "imdb_search" || c.Source != winner.Source || c.Id != winner.Id);
                if (alt is not null)
                {
                    winner = alt;
                    log?.Invoke($"[Winner-alt] \"{winner.Title}\" ({winner.Year}) [{winner.Source}] id={winner.Id}  score={winner.Score:F2}");
                }
            }
        }

        // ── Phase 3: Apply winner ─────────────────────────────────────────────
        // Treat newly-created MediaItems as forceRefresh — the .animarr/<folderHex>/
        // image directory may still contain stale files from a previous, wrong
        // identification on the same folder (e.g. The Gorge 1968 → corrected to 2025
        // but the 1968 poster lingered in cache). The folder hex is keyed on
        // FolderWatcher.Id, which is stable across MediaItem re-creation.
        var imgRefresh = forceRefresh || isNew;
        bool identified = winner.Source switch
        {
            "tmdb_tv"      => await PopulateTvFromTmdbAsync(item, folder, winner.Id, imgRefresh, log, ct),
            "tmdb_movie"   => await PopulateMovieFromTmdbAsync(item, folder, winner.Id, imgRefresh, log, ct),
            "mal"          => await PopulateFromMalAsync(item, folder, winner.Id, log, ct),
            "imdb_search"  => await PopulateFromImdbSearchAsync(item, folder, winner.StringId!, winner.IsTv, imgRefresh, log, ct),
            _              => false,
        };

        if (!identified)
        {
            // If we have other candidates the user can pick from, demote to
            // NeedsReview (banner with top-3) instead of Failed — Populate often
            // returns false for transient TMDB hiccups even when search worked.
            if (candidates.Count > 1)
            {
                log?.Invoke($"[Winner] Populate returned false for source '{winner.Source}' — {candidates.Count - 1} other candidate(s) available, marking NeedsReview.");
                item.IdentificationStatus = IdentificationStatus.NeedsReview;
            }
            else
            {
                log?.Invoke($"[Winner] Populate returned false for source '{winner.Source}' — marking as Failed.");
                item.IdentificationStatus = IdentificationStatus.Failed;
            }
        }
        else
        {
            await FillMissingImagesAsync(item, folder, forceRefresh, log, ct);

            // Phase 2.3: gate the final status on confidence.
            //   ≥ autoApply           → Identified (use as-is, auto-rename)
            //   ≥ needsReview         → NeedsReview (poster shown, badge "needs review", top-3 in CandidatesJson)
            //   < needsReview         → Failed
            var autoThreshold   = await appConfig.GetAsync<double>(AppConfigKeys.AutoApplyConfidence, 0.85, ct);
            var reviewThreshold = await appConfig.GetAsync<double>(AppConfigKeys.NeedsReviewConfidence, 0.50, ct);

            if (winner.Score >= autoThreshold)
            {
                // Keep whatever Populate*Async set (typically Identified).
                if (item.IdentificationStatus is not IdentificationStatus.Manual
                                              and not IdentificationStatus.Identified)
                    item.IdentificationStatus = IdentificationStatus.Identified;
            }
            else if (winner.Score >= reviewThreshold)
            {
                item.IdentificationStatus = IdentificationStatus.NeedsReview;
                log?.Invoke($"[Winner] Confidence {winner.Score:F2} below auto-apply threshold {autoThreshold:F2} — marking NeedsReview.");
            }
            else
            {
                item.IdentificationStatus = IdentificationStatus.Failed;
                log?.Invoke($"[Winner] Confidence {winner.Score:F2} below review threshold {reviewThreshold:F2} — marking Failed.");
            }

            // Auto-pilot completeness: sync FolderWatcher.FolderType with the
            // detected MediaItem.MediaType. Without this the rename pipeline
            // keeps using FolderType=Auto and never produces "Title (Year).mkv"
            // for movie folders, even though we know they're movies.
            if (item.IdentificationStatus == IdentificationStatus.Identified &&
                folder.FolderType == FolderType.Auto)
            {
                var derived = item.MediaType switch
                {
                    MediaItemType.Movie   => FolderType.Movie,
                    MediaItemType.Series  => FolderType.Series,
                    MediaItemType.Anime   => FolderType.Series,
                    _                     => FolderType.Auto,
                };
                if (derived != FolderType.Auto)
                {
                    folder.FolderType = derived;
                    log?.Invoke($"[Auto] FolderType: Auto → {derived} (from MediaType {item.MediaType})");
                }
            }
        }

        item.LastMetadataRefreshedAt = DateTime.UtcNow;
        if (isNew) db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        // Phase 1.2 + 2.3: only auto-rename the containing folder when the final
        // status is Identified (i.e. confidence cleared the auto-apply threshold).
        if (identified && item.IdentificationStatus == IdentificationStatus.Identified)
        {
            var auto = await appConfig.GetAsync<bool>(AppConfigKeys.AutoRenameContainerFolder, true, ct);
            if (auto)
                await TryRenameContainerFolderAsync(folderId, item.Title, item.Year, log, ct);
        }
    }

    /// <summary>
    /// Phase 1.2/2.5: rename the watched folder on disk to "Title (Year)" so the
    /// library stays clean after a torrent dump. Stops the FileSystemWatcher,
    /// moves the directory, updates FolderWatcher.Path / .Label, then restarts
    /// the watcher pointing at the new path. Skipped if any active torrent is
    /// currently writing into this folder.
    /// </summary>
    private async Task TryRenameContainerFolderAsync(
        Guid folderId, string? title, int? year, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folder = await db.FolderWatchers.FindAsync([folderId], ct);
        if (folder is null) return;

        // Never auto-rename a section root — too disruptive (children point at the old name).
        if (folder.IsSection) return;

        // Never auto-rename for flat single-file entries: their .Path points at the
        // section root, and renaming it would clobber every other movie in the section.
        // The file itself is already renamed by the regular RenameService pipeline.
        if (folder.SingleFilePath != null) return;

        var currentDir = folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir  = Path.GetDirectoryName(currentDir);
        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(currentDir)) return;

        var safeTitle = SanitizeForPath(title);
        var targetName = year.HasValue ? $"{safeTitle} ({year})" : safeTitle;
        var targetPath = Path.Combine(parentDir, targetName);

        if (string.Equals(currentDir, targetPath, StringComparison.OrdinalIgnoreCase))
            return; // already correct

        if (Directory.Exists(targetPath))
        {
            log?.Invoke($"[FolderRename] Skip — target already exists: {targetPath}");
            return;
        }

        // Don't move the folder out from under an active torrent — MonoTorrent's
        // SavePath would point at the wrong place.
        if (torrentEngine.IsSavePathActive(currentDir))
        {
            log?.Invoke($"[FolderRename] Skip — active torrent is writing here.");
            return;
        }

        // 2.5: stop FSW BEFORE Directory.Move so file events from the moved tree
        // don't fire on a stale handle. Restart afterwards with the new path.
        bool wasWatching = watcher.IsWatching(folderId);
        if (wasWatching) await watcher.StopWatcherAsync(folderId);

        try
        {
            Directory.Move(currentDir, targetPath);
            folder.Path  = targetPath;
            folder.Label = targetName;
            await db.SaveChangesAsync(ct);
            log?.Invoke($"[FolderRename] {Path.GetFileName(currentDir)} → {targetName}");
            logger.LogInformation("Renamed folder {Old} → {New}", currentDir, targetPath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[FolderRename] Failed: {ex.Message}");
            logger.LogWarning(ex, "Failed to rename folder {Old} → {New}", currentDir, targetPath);
            // Keep folder.Path pointing at currentDir (no save). Fall through to restart watcher.
        }

        // Restart the watcher whether the move succeeded or not — if it failed,
        // the old path still exists and we want to keep watching it.
        if (wasWatching) await watcher.StartWatcherAsync(folderId);
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        return new string(chars).Trim().Trim('.');
    }

    // ── Public: manual identification by numeric ID ───────────────────────────

    public async Task ApplyManualAsync(Guid folderId, string source, int externalId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folder = await db.FolderWatchers.FindAsync([folderId], ct);
        if (folder is null) return;

        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folderId, ct)
                   ?? new MediaItem { Id = Guid.NewGuid(), FolderId = folderId, CreatedAt = DateTime.UtcNow };
        bool isNew = !await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);

        // Clear stale metadata so we get a clean slate
        item.TmdbId = null; item.ImdbId = null; item.TvdbId = null; item.MalId = null;
        item.PosterPath = null; item.FanartPath = null; item.LogoPath = null;

        bool ok = source switch
        {
            "tmdb_tv"    => await PopulateTvFromTmdbAsync(item, folder, externalId, true, null, ct),
            "tmdb_movie" => await PopulateMovieFromTmdbAsync(item, folder, externalId, true, null, ct),
            "mal"        => await PopulateFromMalAsync(item, folder, externalId, null, ct),
            _            => false,
        };

        if (ok)
        {
            await FillMissingImagesAsync(item, folder, true, null, ct);
            item.IdentificationStatus    = IdentificationStatus.Manual;
            item.LastMetadataRefreshedAt = DateTime.UtcNow;
        }

        if (isNew) db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        if (!ok)
        {
            var hint = source.StartsWith("tmdb", StringComparison.Ordinal)
                ? await BuildTmdbErrorHintAsync(externalId.ToString(), source, ct)
                : $"ID «{externalId}» not found in source '{source}'.";
            throw new InvalidOperationException(hint);
        }
    }

    /// <summary>Apply metadata using a string external ID (IMDb "tt...", TVDB integer as string).</summary>
    public async Task ApplyManualAsync(Guid folderId, string source, string externalId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folder = await db.FolderWatchers.FindAsync([folderId], ct);
        if (folder is null) return;

        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folderId, ct)
                   ?? new MediaItem { Id = Guid.NewGuid(), FolderId = folderId, CreatedAt = DateTime.UtcNow };
        bool isNew = !await db.MediaItems.AnyAsync(m => m.Id == item.Id, ct);

        // Clear stale metadata so we get a clean slate
        item.TmdbId = null; item.ImdbId = null; item.TvdbId = null; item.MalId = null;
        item.PosterPath = null; item.FanartPath = null; item.LogoPath = null;

        bool ok = false;
        if (source is "imdb_tv" or "imdb_movie" or "tvdb_tv")
        {
            var findSource = source.StartsWith("imdb") ? "imdb_id" : "tvdb_id";
            var findResult = await tmdb.FindByExternalIdAsync(externalId, findSource, ct);
            if (findResult is not null)
            {
                if (source == "imdb_movie" && findResult.MovieResults.Count > 0)
                    ok = await PopulateMovieFromTmdbAsync(item, folder, findResult.MovieResults[0].Id, true, null, ct);
                else if (findResult.TvResults.Count > 0)
                    ok = await PopulateTvFromTmdbAsync(item, folder, findResult.TvResults[0].Id, true, null, ct);
                else if (findResult.MovieResults.Count > 0)
                    ok = await PopulateMovieFromTmdbAsync(item, folder, findResult.MovieResults[0].Id, true, null, ct);
            }

            // Fallback: populate directly from imdbapi.dev (no TMDB key required)
            if (!ok && source.StartsWith("imdb"))
                ok = await PopulateFromImdbDirectAsync(item, folder, externalId, true, null, ct);

            // Always store the user-entered external ID
            if (source.StartsWith("imdb"))
                item.ImdbId = externalId;
            else if (source == "tvdb_tv" && int.TryParse(externalId, out var tvdbInt))
                item.TvdbId = tvdbInt;
        }

        if (ok)
        {
            await FillMissingImagesAsync(item, folder, true, null, ct);
            item.IdentificationStatus    = IdentificationStatus.Manual;
            item.LastMetadataRefreshedAt = DateTime.UtcNow;
        }

        if (isNew) db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        if (!ok)
            throw new InvalidOperationException(
                $"ID \u00ab{externalId}\u00bb: metadata fetch failed. " +
                $"imdbapi.dev returned no data for this ID (check the ID is correct and starts with 'tt').");
    }

    // ── Public: image picker ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a human-readable error message for a failed TMDB manual lookup.
    /// Distinguishes between: missing/invalid API key, ID exists but wrong media type, ID doesn't exist.
    /// </summary>
    private async Task<string> BuildTmdbErrorHintAsync(string idStr, string source, CancellationToken ct)
    {
        var apiKey = await appConfig.GetAsync(AppConfigKeys.TmdbApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return $"ID «{idStr}»: TMDB API key is not configured (go to Settings → API Keys).";

        // The key exists but the call failed. Give a hint about wrong source.
        var otherSource = source == "tmdb_tv" ? "tmdb_movie" : "tmdb_tv";
        var otherLabel  = source == "tmdb_tv" ? "a Movie" : "a TV Series";
        return $"ID «{idStr}» was not found as {(source == "tmdb_tv" ? "a TV Series" : "a Movie")} on TMDB. " +
               $"If this is {otherLabel}, switch the source dropdown to \"{otherSource}\" and try again.";
    }

    /// <summary>Returns all available poster/backdrop/logo URLs for the item (requires TmdbId or cross-referenceable ImdbId/TvdbId).</summary>
    public async Task<(List<string> Posters, List<string> Backdrops, List<string> Logos)>
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

        if (!item.TmdbId.HasValue)
            return ([], [], []);

        var isTv   = item.MediaType != MediaItemType.Movie;
        var images = isTv
            ? await tmdb.GetTvImagesAsync(item.TmdbId.Value, ct)
            : await tmdb.GetMovieImagesAsync(item.TmdbId.Value, ct);

        if (images is null) return ([], [], []);

        static List<string> ToUrls(List<TmdbImage> list, Func<string, string> urlFn)
            => list.OrderByDescending(i => i.VoteAverage)
                   .Select(i => urlFn(i.FilePath))
                   .ToList();

        return (
            ToUrls(images.Posters,   p => TmdbClient.PosterUrl(p, "w342")),
            ToUrls(images.Backdrops, p => TmdbClient.BackdropUrl(p, "w780")),
            ToUrls(images.Logos,     p => TmdbClient.LogoUrl(p, "w300"))
        );
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
        var relPath  = Path.GetRelativePath(folder.Path, destPath);
        if (!await tmdb.DownloadImageAsync(fullUrl, destPath, ct))
            throw new InvalidOperationException($"Failed to download image from {fullUrl}");

        switch (imageType)
        {
            case "poster": item.PosterPath = relPath; break;
            case "fanart": item.FanartPath = relPath; break;
            case "logo":   item.LogoPath   = relPath; break;
        }
        item.LastMetadataRefreshedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── Candidate gathering ───────────────────────────────────────────────────

    private async Task<List<MetadataCandidate>> GatherCandidatesAsync(
        string searchTitle,
        FolderType typeHint,
        string? tmdbKey,
        string? malKey,
        int? folderYear,
        Action<string>? log,
        CancellationToken ct)
    {
        // Load source order config: [{id:"tmdb_tv",enabled:true},{id:"tmdb_movie",enabled:true}]
        var sourceOrderJson = await appConfig.GetAsync(AppConfigKeys.SearchSourceOrder, ct);
        var sourceOrder = ParseSourceOrder(sourceOrderJson);

        var tasks = new List<Task<List<MetadataCandidate>>>();
        var sourceWeights = new Dictionary<string, double>();

        for (int i = 0; i < sourceOrder.Count; i++)
        {
            var src = sourceOrder[i];
            if (!src.Enabled) continue;
            // weight: first source = 1.0, each subsequent = -0.05
            double weight = 1.0 - i * 0.05;
            sourceWeights[src.Id] = weight;

            if (src.Id == "tmdb_tv" && typeHint != FolderType.Movie)
            {
                if (!string.IsNullOrWhiteSpace(tmdbKey))
                {
                    log?.Invoke($"[TMDB] Searching TV for \"{searchTitle}\"");
                    tasks.Add(SearchTmdbTvCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[TMDB TV] Skipped — API key not configured.");
            }
            else if (src.Id == "tmdb_movie" && typeHint != FolderType.Series)
            {
                if (!string.IsNullOrWhiteSpace(tmdbKey))
                {
                    log?.Invoke($"[TMDB] Searching Movies for \"{searchTitle}\"");
                    tasks.Add(SearchTmdbMovieCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[TMDB Movie] Skipped — API key not configured.");
            }
            else if (src.Id == "mal")
            {
                if (!string.IsNullOrWhiteSpace(malKey))
                {
                    log?.Invoke($"[MAL] Searching for \"{searchTitle}\"");
                    tasks.Add(SearchMalCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[MAL] Skipped — client ID not configured.");
            }
            else if (src.Id == "imdb_search")
            {
                log?.Invoke($"[IMDb] Searching for \"{searchTitle}\"");
                tasks.Add(SearchImdbCandidatesAsync(searchTitle, folderYear, ct, log));
            }
        }

        if (tasks.Count == 0) return [];

        var results = await Task.WhenAll(tasks);
        var all = results.SelectMany(r => r).ToList();

        // Apply source weight to score
        if (sourceWeights.Count > 0)
        {
            all = all.Select(c =>
            {
                double w = sourceWeights.TryGetValue(c.Source, out var wv) ? wv : 1.0;
                return c with { Score = c.Score * w };
            }).ToList();
        }

        // Cross-validation: when two different sources independently return the same
        // work (matched on normalised title + year), boost every candidate in that
        // group by +0.25 — agreement between independent indexes is a strong signal.
        if (all.Count > 1)
        {
            var groups = all
                .GroupBy(c => (Key: NormaliseTitleForMatch(c.Title), c.Year))
                .Where(g => g.Select(c => c.Source).Distinct().Count() >= 2)
                .ToList();

            if (groups.Count > 0)
            {
                var boosted = new HashSet<MetadataCandidate>(ReferenceEqualityComparer.Instance);
                foreach (var g in groups)
                    foreach (var c in g)
                        boosted.Add(c);
                all = all.Select(c => boosted.Contains(c)
                    ? c with { Score = c.Score + 0.25 }
                    : c).ToList();
                log?.Invoke($"[Cross-validation] {groups.Count} cross-source match group(s) boosted (+0.25)");
            }
        }

        log?.Invoke($"[Search] {all.Count} total candidates");
        return all;
    }

    /// <summary>Strips non-alphanumeric characters and lower-cases — for cross-source matching.</summary>
    private static string NormaliseTitleForMatch(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static List<(string Id, bool Enabled)> ParseSourceOrder(string? json)
    {
        var defaults = new List<(string, bool)> { ("tmdb_tv", true), ("tmdb_movie", true), ("mal", false), ("imdb_search", true) };
        if (string.IsNullOrWhiteSpace(json)) return defaults;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SearchSourceEntry>>(json);
            if (parsed is { Count: > 0 })
                return parsed.Select(e => (e.Id, e.Enabled)).ToList();
        }
        catch { /* ignore malformed config */ }
        return defaults;
    }

    private sealed record SearchSourceEntry(string Id, bool Enabled);

    private async Task<List<MetadataCandidate>> SearchTmdbTvCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await tmdb.SearchTvAsync(searchTitle, ct);
        return results.Take(5).Select(r => new MetadataCandidate(
            Source:        "tmdb_tv",
            Id:            r.Id,
            Title:         r.DisplayTitle,
            OriginalTitle: r.OriginalName,
            Year:          r.Year,
            Overview:      r.Overview,
            IsTv:          true,
            Score:         ScoreResult(r.DisplayTitle, r.OriginalName, r.Year, searchTitle, folderYear)
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchTmdbMovieCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await tmdb.SearchMovieAsync(searchTitle, ct);
        return results.Take(5).Select(r => new MetadataCandidate(
            Source:        "tmdb_movie",
            Id:            r.Id,
            Title:         r.DisplayTitle,
            OriginalTitle: r.OriginalTitle,
            Year:          r.Year,
            Overview:      r.Overview,
            IsTv:          false,
            Score:         ScoreResult(r.DisplayTitle, r.OriginalTitle, r.Year, searchTitle, folderYear)
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchMalCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await mal.SearchAsync(searchTitle, 5, ct);
        return results.Select(r => new MetadataCandidate(
            Source:        "mal",
            Id:            r.Id,
            Title:         r.EnglishTitle,
            OriginalTitle: r.AlternativeTitles?.Ja ?? r.Title,
            Year:          r.Year,
            Overview:      r.Synopsis,
            IsTv:          true,
            Score:         ScoreResult(r.EnglishTitle, r.AlternativeTitles?.Ja ?? r.Title, r.Year, searchTitle, folderYear)
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchImdbCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct, Action<string>? log = null)
    {
        var results = await imdbSearch.SearchTitlesAsync(searchTitle, 5, ct);
        if (results.Count == 0)
            log?.Invoke($"[IMDb] No results for \"{{searchTitle}}\" — ensure the title is in English.");
        return results.Select(r =>
        {
            bool isTv = r.Type is "tvSeries" or "tvMiniSeries" or "tvSpecial" or "tvMovie";
            return new MetadataCandidate(
                Source:        "imdb_search",
                Id:            0,
                Title:         r.PrimaryTitle,
                OriginalTitle: r.OriginalTitle,
                Year:          r.StartYear,
                Overview:      null,
                IsTv:          isTv,
                Score:         ScoreResult(r.PrimaryTitle, r.OriginalTitle, r.StartYear, searchTitle, folderYear),
                StringId:      r.Id);
        }).ToList();
    }

    private static double ScoreResult(string title, string? altTitle, int? year, string searchTitle, int? folderYear)
    {
        double sim = StringSimilarity(title, searchTitle);
        if (!string.IsNullOrEmpty(altTitle))
            sim = Math.Max(sim, StringSimilarity(altTitle, searchTitle) * 0.95);
        double score = sim * 2.0;

        if (year.HasValue && folderYear.HasValue)
        {
            if (year == folderYear) score += 0.4;
            else if (Math.Abs(year.Value - folderYear.Value) <= 1) score += 0.15;
        }
        return score;
    }

    // ── LLM winner selection ──────────────────────────────────────────────────

    private async Task<MetadataCandidate> SelectWinnerAsync(
        List<MetadataCandidate> candidates,
        string folderPath,
        int? expectedYear,
        Action<string>? log,
        CancellationToken ct)
    {
        var sorted = candidates.OrderByDescending(c => c.Score).ToList();

        // Log top-3 for the scan log
        for (int i = 0; i < Math.Min(sorted.Count, 3); i++)
            log?.Invoke($"  [{i}] \"{sorted[i].Title}\" ({sorted[i].Year}) [{sorted[i].Source}] score={sorted[i].Score:F2}");

        if (sorted.Count == 1)
        {
            // Single candidate: trust TMDB's unique match even when string-similarity
            // is zero (typical for pinyin/romaji folder names vs English TMDB titles).
            // Boost to the auto-apply threshold so the result isn't flagged as Failed
            // purely because of language mismatch.
            var solo = sorted[0];
            log?.Invoke($"[Score] Single candidate — trusting it: \"{solo.Title}\"");
            return solo.Score >= 0.85 ? solo : solo with { Score = 0.85 };
        }

        // Year-anchored shortcut: if we know the expected year and at least one
        // candidate matches it ±1 year, hide all other-year candidates from the
        // LLM. Prevents the LLM (qwen2.5:1.5b) from picking a 1968 film when the
        // file name clearly says 2025.
        var yearMatching = expectedYear.HasValue
            ? sorted.Where(c => c.Year.HasValue && Math.Abs(c.Year.Value - expectedYear.Value) <= 1).ToList()
            : new List<MetadataCandidate>();
        if (yearMatching.Count > 0 && expectedYear.HasValue)
        {
            log?.Invoke($"[Score] Filtering candidates by year ±1 of {expectedYear.Value}: {yearMatching.Count} match(es) remain.");
            sorted = yearMatching;
            if (sorted.Count == 1)
            {
                var solo = sorted[0];
                return solo.Score >= 0.85 ? solo : solo with { Score = 0.85 };
            }
        }

        var llmEnabled = await appConfig.GetAsync<bool>(AppConfigKeys.LlmEnabled, false, ct);
        if (!llmEnabled) return sorted[0];

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var topN = sorted.Take(5).ToList();

        try
        {
            var llmIndex = await llm.SelectCandidateAsync(
                folderName,
                topN.Select((c, i) => new LlmCandidateItem
                {
                    Index    = i,
                    Source   = c.Source,
                    Title    = c.Title,
                    Year     = c.Year,
                    Type     = c.Source == "mal" ? "anime" : (c.IsTv ? "tv" : "movie"),
                    Overview = c.Overview,
                }).ToList(),
                ct);

            if (llmIndex.HasValue && llmIndex.Value >= 0 && llmIndex.Value < topN.Count)
            {
                var picked = topN[llmIndex.Value];
                log?.Invoke($"[LLM] Selected [{llmIndex.Value}]: \"{picked.Title}\" [{picked.Source}]");
                // Trust LLM's pick when the underlying signal is decent. We boost to
                // 0.9 (clear of the auto-apply threshold) only if either:
                //   • title-similarity is reasonable (base score ≥ 0.7), OR
                //   • the year matches our expectation (+0.4 from year alone).
                // For weak matches (Kaiju Jiu → Kaiju Girls, score 0.42) we keep the
                // raw score so the result lands in NeedsReview with a top-3 banner.
                bool trustLlm = picked.Score >= 0.7
                    || (expectedYear.HasValue && picked.Year.HasValue
                        && Math.Abs(picked.Year.Value - expectedYear.Value) <= 1);
                if (trustLlm)
                    return picked.Score >= 0.9 ? picked : picked with { Score = 0.9 };
                log?.Invoke($"[LLM] Selected pick has weak base score ({picked.Score:F2}) — keeping for NeedsReview.");
                return picked;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM candidate selection failed, using top scorer");
        }

        log?.Invoke($"[Score] Top scorer: \"{sorted[0].Title}\" [{sorted[0].Source}]");
        return sorted[0];
    }

    // ── TMDB populate ─────────────────────────────────────────────────────────

    private async Task<bool> PopulateTvFromTmdbAsync(
        MediaItem item, FolderWatcher folder, int tmdbId, bool forceRefresh,
        Action<string>? log, CancellationToken ct)
    {
        var detail = await tmdb.GetTvDetailAsync(tmdbId, ct);
        if (detail is null) { log?.Invoke($"[TMDB] GetTvDetail({tmdbId}) returned null."); return false; }

        log?.Invoke($"[TMDB] TV detail: \"{detail.Name}\" ({detail.Year})  seasons={detail.Seasons.Count}");

        item.TmdbId        = detail.Id;
        item.ImdbId        = detail.ExternalIds?.ImdbId;
        item.TvdbId        = detail.ExternalIds?.TvdbId;
        item.Title         = detail.Name;
        item.OriginalTitle = detail.OriginalName;
        item.Year          = detail.Year;
        item.Description   = detail.Overview;
        item.Tagline       = detail.Tagline;
        item.Status        = detail.Status;
        item.ContentRating = detail.ContentRating;
        item.Rating        = detail.VoteAverage > 0 ? detail.VoteAverage : null;
        item.RatingCount   = detail.VoteCount > 0 ? detail.VoteCount : null;
        item.Runtime       = detail.EpisodeRunTime.FirstOrDefault();
        item.GenresJson    = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);
        item.MediaType     = MediaItemType.Series;

        // Seasons — include PosterPath so Explorer can show thumbnails
        var seasons = detail.Seasons
            .Where(s => s.SeasonNumber > 0)
            .Select(s => new SeasonMeta
            {
                Number       = s.SeasonNumber,
                EpisodeCount = s.EpisodeCount,
                Name         = s.Name,
                PosterPath   = s.PosterPath != null
                    ? Path.Combine(".animarr", $"season{s.SeasonNumber}-poster.jpg")
                    : null,
            }).ToList();
        item.SeasonsJson = JsonSerializer.Serialize(seasons, _json);

        item.IdentificationStatus = IdentificationStatus.Identified;

        // Main images
        await DownloadImagesAsync(item, folder,
            poster:       detail.PosterPath     != null ? TmdbClient.PosterUrl(detail.PosterPath)         : null,
            fanart:       detail.BestFanartPath != null ? TmdbClient.BackdropUrl(detail.BestFanartPath)   : null,
            logo:         detail.BestLogoPath   != null ? TmdbClient.LogoUrl(detail.BestLogoPath)         : null,
            forceRefresh: forceRefresh, log: log, ct: ct);

        // Season posters → .animarr/seasonN-poster.jpg
        foreach (var s in detail.Seasons.Where(s => s.SeasonNumber > 0 && s.PosterPath != null))
        {
            var dest = Path.Combine(folder.Path, ".animarr", $"season{s.SeasonNumber}-poster.jpg");
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
        var detail = await tmdb.GetMovieDetailAsync(tmdbId, ct);
        if (detail is null) { log?.Invoke($"[TMDB] GetMovieDetail({tmdbId}) returned null."); return false; }

        log?.Invoke($"[TMDB] Movie detail: \"{detail.Title}\" ({detail.Year})");

        item.TmdbId        = detail.Id;
        item.ImdbId        = detail.ExternalIds?.ImdbId;
        item.TvdbId        = detail.ExternalIds?.TvdbId;
        item.Title         = detail.Title;
        item.OriginalTitle = detail.OriginalTitle;
        item.Year          = detail.Year;
        item.Description   = detail.Overview;
        item.Tagline       = detail.Tagline;
        item.Status        = detail.Status;
        item.ContentRating = detail.ContentRating;
        item.Rating        = detail.VoteAverage > 0 ? detail.VoteAverage : null;
        item.RatingCount   = detail.VoteCount > 0 ? detail.VoteCount : null;
        item.Runtime       = detail.Runtime;
        item.GenresJson    = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);
        item.MediaType     = MediaItemType.Movie;

        item.IdentificationStatus = IdentificationStatus.Identified;

        await DownloadImagesAsync(item, folder,
            poster:       detail.PosterPath   != null ? TmdbClient.PosterUrl(detail.PosterPath)     : null,
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
        item.Year          ??= detail.Year;
        item.Description   ??= detail.Synopsis;
        if (item.Rating is null && detail.Mean.HasValue)               item.Rating      = detail.Mean;
        if (item.RatingCount is null && detail.NumScoringUsers.HasValue) item.RatingCount = detail.NumScoringUsers;
        if (detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);

        item.MediaType            = MediaItemType.Anime;
        item.IdentificationStatus = IdentificationStatus.Identified;

        if (item.PosterPath is null && detail.PosterUrl is not null)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            var rel      = Path.Combine(".animarr", "poster.jpg");
            if (!File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading MAL poster → {rel}");
                if (await tmdb.DownloadImageAsync(detail.PosterUrl, destPath, ct))
                { item.PosterPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
                else
                { log?.Invoke($"[Images] {rel} ✗ (download failed)"); }
            }
            else
            {
                item.PosterPath = rel;
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
        }
        if (detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres, _json);

        bool isTv = detail.Type is "tvSeries" or "tvMiniSeries" or "tvSpecial";
        item.MediaType = isTv ? MediaItemType.Series : MediaItemType.Movie;
        item.IdentificationStatus = IdentificationStatus.Identified;

        // Download poster from imdbapi.dev primaryImage if available
        if (detail.PrimaryImage?.Url is { Length: > 0 } posterUrl)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            var rel      = Path.Combine(".animarr", "poster.jpg");
            if (forceRefresh || item.PosterPath is null || !File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading IMDb poster → {rel}");
                if (await tmdb.DownloadImageAsync(posterUrl, destPath, ct))
                { item.PosterPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
                else
                { log?.Invoke($"[Images] {rel} ✗ (download failed)"); }
            }
            else if (File.Exists(destPath))
            {
                item.PosterPath = rel;
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
        if (item.Year is null)                         item.Year          = detail.Year;
        if (item.Description is null)                  item.Description   = detail.Synopsis;
        if (item.Rating is null && detail.Mean.HasValue)               item.Rating     = detail.Mean;
        if (item.RatingCount is null && detail.NumScoringUsers.HasValue) item.RatingCount = detail.NumScoringUsers;
        if (item.GenresJson is null && detail.Genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);

        item.MediaType = MediaItemType.Anime;

        if (item.PosterPath is null && detail.PosterUrl is not null)
        {
            var metaDir  = MetaDir(folder);
            var destPath = Path.Combine(metaDir, "poster.jpg");
            var rel      = Path.Combine(".animarr", "poster.jpg");
            if (forceRefresh || !File.Exists(destPath))
            {
                log?.Invoke($"[Images] Downloading MAL poster → {rel}");
                if (await tmdb.DownloadImageAsync(detail.PosterUrl, destPath, ct))
                { item.PosterPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
            }
            else
            {
                item.PosterPath = rel;
            }
        }
    }

    // ── Image download ────────────────────────────────────────────────────────

    private static string MetaDir(FolderWatcher folder)
    {
        var dir = folder.SingleFilePath != null
            ? Path.Combine(folder.Path, ".animarr", folder.Id.ToString("N"))
            : Path.Combine(folder.Path, ".animarr");
        Directory.CreateDirectory(dir);
        return dir;
    }

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
            var rel  = Path.GetRelativePath(folder.Path, dest);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading poster → {rel}");
                if (await tmdb.DownloadImageAsync(poster, dest, ct))
                { item.PosterPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
                else
                { log?.Invoke($"[Images] {rel} ✗ (download failed)"); }
            }
            else
            {
                item.PosterPath = rel;
                log?.Invoke($"[Images] {rel} already exists, skipping");
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
            var rel  = Path.GetRelativePath(folder.Path, dest);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading fanart → {rel}");
                if (await tmdb.DownloadImageAsync(fanart, dest, ct))
                { item.FanartPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
                else
                { log?.Invoke($"[Images] {rel} ✗ (download failed)"); }
            }
            else
            {
                item.FanartPath = rel;
                log?.Invoke($"[Images] {rel} already exists, skipping");
            }
        }

        if (logo != null)
        {
            var ext  = Path.GetExtension(logo.Split('?')[0]);
            var name = "logo" + (string.IsNullOrEmpty(ext) ? ".png" : ext);
            var dest = Path.Combine(metaDir, name);
            var rel  = Path.GetRelativePath(folder.Path, dest);
            if (forceRefresh || !File.Exists(dest))
            {
                log?.Invoke($"[Images] Downloading logo → {rel}");
                if (await tmdb.DownloadImageAsync(logo, dest, ct))
                { item.LogoPath = rel; log?.Invoke($"[Images] {rel} ✓"); }
                else
                { log?.Invoke($"[Images] {rel} ✗ (download failed)"); }
            }
            else
            {
                item.LogoPath = rel;
                log?.Invoke($"[Images] {rel} already exists, skipping");
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
                    var d = await tmdb.GetTvDetailAsync(tmdbId.Value, ct);
                    if (d is not null)
                    {
                        if (needPoster && d.PosterPath     != null) posterUrl = TmdbClient.PosterUrl(d.PosterPath);
                        if (needFanart && d.BestFanartPath != null) fanartUrl = TmdbClient.BackdropUrl(d.BestFanartPath);
                        if (needLogo   && d.BestLogoPath   != null) logoUrl   = TmdbClient.LogoUrl(d.BestLogoPath);
                    }
                }
                else
                {
                    var d = await tmdb.GetMovieDetailAsync(tmdbId.Value, ct);
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
                var rel      = Path.Combine(".animarr", "poster.jpg");
                if (forceRefresh || !File.Exists(destPath))
                {
                    if (await tmdb.DownloadImageAsync(posterUrl, destPath, ct))
                    { item.PosterPath = rel; log?.Invoke($"[Images/Fallback] {rel} from MAL ✓"); }
                    else
                    { log?.Invoke($"[Images/Fallback] {rel} from MAL ✗"); }
                }
                else
                {
                    item.PosterPath = rel;
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ParseTitleFromPath(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\[\(]\d{4}[\]\)]?\s*$", "").Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*-?\s*S\d{1,2}(E\d{1,2})?(\s*-\s*S?\d{1,2}E\d{1,2})?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+(Season|Series|Part)\s*\d+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+(st|nd|rd|th)\s+Season.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\[\(](1080p|720p|480p|4K|UHD|BluRay|BDRip|WEB-DL|WEBRip|HEVC|x265|x264|AVC|AAC|DTS|FLAC|HDR|SDR).*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = name.Replace('.', ' ').Replace('_', ' ');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s{2,}", " ").Trim();
        // Strip trailing standalone 4-digit year (e.g. "Movie Name 2024" from "Movie.Name.2024")
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s(19|20)\d{2}\s*$", "").Trim();
        return name;
    }

    private static int? ExtractYearFromPath(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
        // Bracketed year: (2024) or [2024]
        var m = System.Text.RegularExpressions.Regex.Match(name, @"[\[\(](\d{4})[\]\)]");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var y) && y is >= 1900 and <= 2099)
            return y;
        // Dot/space/dash-separated trailing year: Movie.Name.2024 or Movie Name - 2024
        var m2 = System.Text.RegularExpressions.Regex.Match(name, @"[.\s\-]((?:19|20)\d{2})(?:[.\s]|$)");
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var y2) && y2 is >= 1900 and <= 2099)
            return y2;
        return null;
    }

    private static double StringSimilarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.8;

        static HashSet<string> Bigrams(string s) =>
            [.. Enumerable.Range(0, Math.Max(0, s.Length - 1)).Select(i => s.Substring(i, 2))];

        var ba = Bigrams(a);
        var bb = Bigrams(b);
        if (ba.Count == 0 || bb.Count == 0) return 0;
        double intersection = ba.Intersect(bb).Count();
        return 2.0 * intersection / (ba.Count + bb.Count);
    }
}

/// <summary>Season metadata stored as JSON in MediaItem.SeasonsJson</summary>
public class SeasonMeta
{
    public int Number { get; set; }
    public int EpisodeCount { get; set; }
    public string? Name { get; set; }
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    public string? AirDate { get; set; }
}