using System.Text.Json;
using Animarr.Shared.Models;
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
    MediaCachePaths cachePaths,
    ILogger<MetadataService> logger)
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>Fires after a MediaItem's metadata has been changed (manual apply, basics save,
    /// re-identify, image swap). Subscribers re-query their slice — Catalog grid refreshes the
    /// poster, NeedsReview chip re-counts, MediaDetail page re-loads. Pure push, no payload —
    /// subscribers go back to the DB for the new state.</summary>
    public event Action<Guid>? MediaItemChanged;

    /// <summary>Raise the change notification — called by ApplyManualAsync, ApplySelectedImageAsync,
    /// and any other write-path that needs to signal UI subscribers.</summary>
    public void NotifyMediaItemChanged(Guid folderId) => MediaItemChanged?.Invoke(folderId);

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
        string? StringId   = null,   // non-integer IDs (e.g. IMDb "tt...")
        string? PosterUrl  = null);  // small thumb URL for the NeedsReview UI

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

        // Search-title priority: when the LLM gives an English/romanised title (english_title)
        // AND its primary title is non-ASCII (e.g. Chinese 凡人修仙传, Japanese 仙逆), search by
        // the English form first — TMDB/IMDb don't index CJK well, and MAL's hits for CJK are
        // noisy (a Chinese title bag-of-chars matches dozens of unrelated anime).
        string searchTitle;
        if (llmResult is { EnglishTitle: { Length: > 0 } eng } && !IsMostlyAscii(llmResult.Title ?? "") && IsMostlyAscii(eng))
            searchTitle = eng;
        else
            searchTitle = llmResult?.Title ?? ParseTitleFromPath(titleSource);
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
        // When we prefer english_title for searching, surface that — otherwise the
        // [TMDB]/[IMDb] log lines look mysterious (searching for a title the LLM
        // didn't claim to identify).
        if (llmResult?.Title is { } llmTitle &&
            !string.Equals(llmTitle, searchTitle, StringComparison.Ordinal))
            log?.Invoke($"[Search] Preferring English/romanised title for search: \"{searchTitle}\"");
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
                Id        = c.Id,
                StringId  = c.StringId,    // IMDb "tt..." ids — needed for external link
                Title     = c.Title,
                Year      = c.Year,
                Type      = c.Source,
                IsTv      = c.IsTv,        // needed by UI to dispatch imdb_search → imdb_tv vs imdb_movie
                Overview  = c.Overview,
                PosterUrl = c.PosterUrl,
            }).ToList(), _json);

        // ── Phase 2: Pick winner (LLM or top-scorer) ─────────────────────────
        var winner = await SelectWinnerAsync(candidates, folder.Path, folderYear, typeHint, log, ct);
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
            // Auto-apply default raised from 0.85 → 0.95 after the user observed
            // false-positives sneaking in around 85-90% confidence (LLM-normalised
            // weak base scores were flooring at the review threshold). At 0.95
            // only TMDB hits with 475+ votes or strong LLM+source agreement pass
            // auto-apply; everything else lands in NeedsReview with top-3
            // candidates for manual review via the design's NR modal.
            var autoThreshold   = await appConfig.GetAsync<double>(AppConfigKeys.AutoApplyConfidence, 0.95, ct);
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

        // Container-folder auto-rename was removed entirely after two catastrophic
        // data-corruption incidents (Movies → 6 wrong-named folders, Bleach → "The
        // Portal"). Identification now only updates the DB association
        // (FolderWatcher.Id ↔ MediaItem.FolderId, MediaItem.Title, …); the
        // on-disk path and folder.Label are never modified by identification.
        // If the user wants the folder renamed they can do it themselves through
        // the file manager.
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
        // New design fields: clear too so a re-identify against a different source can't carry over
        // a stale studio/language/tags string from the previous match.
        item.Studio = null; item.Language = null; item.EpisodeCount = null; item.SeasonLabel = null;
        item.CjkTitle = null; item.EnglishTitle = null; item.TagsJson = null;
        item.TmdbConfidence = null; item.MalConfidence = null; item.ImdbConfidence = null;
        // Hue is intentionally preserved across re-identify — the user may have manually edited it,
        // and the same title should keep its tint even after switching source.

        bool ok = source switch
        {
            "tmdb_tv"    => await PopulateTvFromTmdbAsync(item, folder, externalId, true, null, ct),
            "tmdb_movie" => await PopulateMovieFromTmdbAsync(item, folder, externalId, true, null, ct),
            "mal"        => await PopulateFromMalAsync(item, folder, externalId, null, ct),
            _            => false,
        };

        // TMDB IDs don't overlap between /tv/ and /movie/, but a folder's
        // initial MediaType guess (which decides which endpoint we tried first)
        // is frequently wrong — a "Movies" section can contain a series and
        // vice versa. So if the first endpoint 404'd, transparently try the
        // other one. PopulateXxxFromTmdb sets MediaType to Series/Movie itself,
        // so the item ends up correctly typed regardless of the folder's guess.
        if (!ok && source == "tmdb_tv")
        {
            ok = await PopulateMovieFromTmdbAsync(item, folder, externalId, true, null, ct);
            if (ok) source = "tmdb_movie";
        }
        else if (!ok && source == "tmdb_movie")
        {
            ok = await PopulateTvFromTmdbAsync(item, folder, externalId, true, null, ct);
            if (ok) source = "tmdb_tv";
        }

        if (ok)
        {
            await FillMissingImagesAsync(item, folder, true, null, ct);
            item.IdentificationStatus    = IdentificationStatus.Manual;
            item.LastMetadataRefreshedAt = DateTime.UtcNow;
        }

        if (isNew) db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        if (ok) NotifyMediaItemChanged(folderId);
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
        // New design fields: clear too so a re-identify against a different source can't carry over
        // a stale studio/language/tags string from the previous match.
        item.Studio = null; item.Language = null; item.EpisodeCount = null; item.SeasonLabel = null;
        item.CjkTitle = null; item.EnglishTitle = null; item.TagsJson = null;
        item.TmdbConfidence = null; item.MalConfidence = null; item.ImdbConfidence = null;
        // Hue is intentionally preserved across re-identify — the user may have manually edited it,
        // and the same title should keep its tint even after switching source.

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

    /// <summary>True when ≥70% of the letter characters are basic ASCII — used to decide
    /// whether to switch to the LLM's english_title for searching.</summary>
    private static bool IsMostlyAscii(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        int letters = 0, ascii = 0;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c < 128) ascii++;
        }
        return letters == 0 || ascii * 10 >= letters * 7;
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

    // TMDB poster thumb size for the NeedsReview UI (≈ 60×90 rendered).
    private const string TmdbThumbBase = "https://image.tmdb.org/t/p/w154";

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
            Score:         ScoreResult(r.DisplayTitle, r.OriginalName, r.Year, searchTitle, folderYear),
            PosterUrl:     !string.IsNullOrEmpty(r.PosterPath) ? TmdbThumbBase + r.PosterPath : null
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
            Score:         ScoreResult(r.DisplayTitle, r.OriginalTitle, r.Year, searchTitle, folderYear),
            PosterUrl:     !string.IsNullOrEmpty(r.PosterPath) ? TmdbThumbBase + r.PosterPath : null
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
            Score:         ScoreResult(r.EnglishTitle, r.AlternativeTitles?.Ja ?? r.Title, r.Year, searchTitle, folderYear),
            PosterUrl:     r.PosterUrl
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchImdbCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct, Action<string>? log = null)
    {
        var results = await imdbSearch.SearchTitlesAsync(searchTitle, 5, ct);
        if (results.Count == 0)
            log?.Invoke($"[IMDb] No results for \"{searchTitle}\" — ensure the title is in English.");
        // Note: IMDb's PrimaryImage is only on the detail endpoint, not the
        // search response — we leave PosterUrl null and rely on the external
        // link button in the NeedsReview UI to let the user preview manually.
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
        FolderType typeHint,
        Action<string>? log,
        CancellationToken ct)
    {
        // Type filter: when caller asks for Series (e.g. LLM said anime), drop
        // IMDb-source movie candidates so the LLM can't pick them. tmdb_movie
        // is already excluded upstream in GatherCandidatesAsync, but IMDb's
        // mixed type-set still leaks movies into the candidate pool.
        var typed = candidates;
        if (typeHint == FolderType.Series)
            typed = candidates.Where(c => c.IsTv || c.Source != "imdb_search").ToList();
        else if (typeHint == FolderType.Movie)
            typed = candidates.Where(c => !c.IsTv || c.Source != "imdb_search").ToList();
        if (typed.Count == 0) typed = candidates;          // never strand the show

        var sorted = typed.OrderByDescending(c => c.Score).ToList();

        // Log top-5 — that's the slice the LLM actually sees in SelectCandidate.
        // (Previously only top-3 were logged, which made it confusing when the LLM
        //  picked index 4 and the log didn't show what was there.)
        for (int i = 0; i < Math.Min(sorted.Count, 5); i++)
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

                // TMDB preference: when LLM picks a non-TMDB source but the top
                // scorer is TMDB and the score difference is small (≤ 0.3), keep
                // the TMDB pick. TMDB has richer data (seasons, episode lists,
                // episode names + stills) — MAL-only matches give the user an
                // empty season tab in MediaDetail, while TMDB-matches render the
                // full episode list with ✓/download badges.
                var top = sorted[0];
                bool topIsTmdb = top.Source == "tmdb_tv" || top.Source == "tmdb_movie";
                bool pickedIsTmdb = picked.Source == "tmdb_tv" || picked.Source == "tmdb_movie";
                if (topIsTmdb && !pickedIsTmdb && top.Score - picked.Score <= 0.3)
                {
                    log?.Invoke($"[Score] Overriding LLM pick — top scorer {top.Source} is TMDB (ΔScore={top.Score - picked.Score:F2}).");
                    picked = top;
                }

                // Trust LLM's pick when the underlying signal is decent. We boost to
                // 0.9 (clear of the auto-apply threshold) only if either:
                //   • title-similarity is reasonable (base score ≥ 0.7), OR
                //   • the year matches our expectation (+0.4 from year alone).
                // For weak matches (Kaiju Jiu → Kaiju Girls, score 0.42) we keep
                // the LLM choice but FLOOR the score at the NeedsReview threshold
                // so the result lands in NeedsReview with a top-3 banner — never
                // Failed (which hides the banner and strands the user).
                bool trustLlm = picked.Score >= 0.7
                    || (expectedYear.HasValue && picked.Year.HasValue
                        && Math.Abs(picked.Year.Value - expectedYear.Value) <= 1);
                if (trustLlm)
                    return picked.Score >= 0.9 ? picked : picked with { Score = 0.9 };

                // The NeedsReview floor is the same value the caller compares against
                // in IdentifyFolderAsync (default 0.50). Use 0.50 as the floor here —
                // even if the user has lowered NeedsReviewConfidence further, 0.50
                // is still above any reasonable failure cutoff and ensures the banner.
                const double NeedsReviewFloor = 0.50;
                if (picked.Score < NeedsReviewFloor)
                {
                    log?.Invoke($"[LLM] Weak base score ({picked.Score:F2}) — floored to {NeedsReviewFloor:F2} so user can review.");
                    return picked with { Score = NeedsReviewFloor };
                }
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
        item.Popularity    = detail.Popularity > 0 ? detail.Popularity : null;
        item.Runtime       = detail.EpisodeRunTime.FirstOrDefault();
        item.GenresJson    = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);
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

        // Main images
        await DownloadImagesAsync(item, folder,
            poster:       detail.PosterPath     != null ? TmdbClient.PosterUrl(detail.PosterPath)         : null,
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
        item.Popularity    = detail.Popularity > 0 ? detail.Popularity : null;
        item.Runtime       = detail.Runtime;
        item.GenresJson    = JsonSerializer.Serialize(detail.Genres.Select(g => g.Name).ToList(), _json);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ParseTitleFromPath(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

        // 1. Bracketed year — strip the bracket cluster only (year still extracted by ExtractYearFromPath)
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\[\(]\d{4}[\]\)]?\s*$", "").Trim();

        // 2. Season/episode markers (TV files that slipped into a Movies section)
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*-?\s*S\d{1,2}(E\d{1,2})?(\s*-\s*S?\d{1,2}E\d{1,2})?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+(Season|Series|Part)\s*\d+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+(st|nd|rd|th)\s+Season.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // 3. Bracketed release-tag cluster (e.g. "(1080p BluRay x265)") — strip from the marker on
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\[\(](1080p|720p|480p|2160p|4K|UHD|BluRay|BDRip|WEB-DL|WEBRip|HEVC|x265|x264|AVC|AAC|DTS|FLAC|HDR|SDR).*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // 4. Normalise separators — dots/underscores → spaces, then collapse runs.
        name = name.Replace('.', ' ').Replace('_', ' ');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s{2,}", " ").Trim();

        // 5. Release-noise word/year stripping. Many torrent-style filenames have the title +
        //    a dot-separated release-tag run: "Соник 2 в кино 2022 UHD Blu-Ray Remux 2160p"
        //    becomes "Соник 2 в кино" after this pass. Walk from the right, drop each token
        //    that looks like noise; stop at the first token that doesn't match.
        //    The earlier `\s(19|20)\d{2}\s*$` rule only caught the year if it was already
        //    the trailing token after the regexes above — for files with year + release tags
        //    after it, it missed.
        var noise = new System.Text.RegularExpressions.Regex(
            @"^(1080p|720p|480p|2160p|4K|UHD|BluRay|Blu-Ray|BDRip|BRRip|DVDRip|WEB-?DL|WEBRip|HEVC|x265|x264|H\.?265|H\.?264|AVC|AAC|AC3|DTS(?:-HD)?|TrueHD|FLAC|HDR|HDR10\+?|SDR|10bit|8bit|REMUX|Atmos|2CH|6CH|MA|Dolby|Hybrid|Extended|Director'?s?Cut|UNRATED|Theatrical|REPACK|PROPER|MULTi|DUAL|RUS|ENG|JAP|CHS|CHT|EN|RU|JA|ZH|KO|FR|DE|ES|IT|SUB|SUBS|DUB|DUBBED|FANSUB|YIFY|YTS|RARBG|FGT|EVO|CMRG|GalaxyRG|TGx|d3g|Telesync|TS|CAM|HDCAM|TC|TBS|VC-?1|10-?bit|HQ|HDTV|SDTV|BDR|BDRemux|REMASTERED|MAR-CAS|MeGusta|EPSiLON|SPARKS|NTb|FraMeSToR|DEFLATE|tigole|UTR|d3g)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var year = new System.Text.RegularExpressions.Regex(@"^(19|20)\d{2}$");

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0)
        {
            var last = tokens[^1];
            if (noise.IsMatch(last) || year.IsMatch(last))
            {
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }
            break;
        }
        name = string.Join(' ', tokens).Trim();

        // Trailing punctuation that survived the strip.
        name = name.Trim('-', '.', ' ', '_');
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