using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
// Shared helpers moved to Animarr.Shared. Aliased (not `using Animarr.Shared`)
// so the shared enum copies there don't clash with the Data.Models entities.
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

/// <summary>
/// Orchestrates metadata lookup: TMDB (series/movies) + MAL (anime).
/// Searches all enabled sources in parallel, scores results, optionally uses LLM
/// for final selection, then downloads images.
/// Called by IdentificationQueueProcessorService after the optional LLM title-normalisation step.
/// </summary>
public partial class MetadataService(
    IDbContextFactory<AppDbContext> dbFactory,
    TmdbClient tmdb,
    MalClient mal,
    ImdbSearchClient imdbSearch,
    AnimeThemesClient animeThemes,
    AniListClient aniList,
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

        var tmdbKey = await appConfig.GetTmdbApiKeyAsync(ct);
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

        // Save top-3 for the UI NeedsReview view. Field names MUST match
        // IdentificationCandidateDto (Source / ExternalId / Title / OriginalTitle /
        // Year / PosterUrl / Overview / Confidence) — the client deserialises the
        // column straight into that record. The old anonymous shape saved Source
        // as "Type", the id as "Id"/"StringId" (DTO wants "ExternalId"), and never
        // wrote the score at all, so every candidate came back with source=null,
        // externalId=null, confidence=0 → the NeedsReview "Use" button was a no-op
        // and the dialog NRE'd on cand.Source.ToUpperInvariant().
        item.CandidatesJson = JsonSerializer.Serialize(
            candidates.OrderByDescending(c => c.Score).Take(3).Select(c => new
            {
                Source        = c.Source,                      // "tmdb_tv" | "tmdb_movie" | "mal" | "imdb_search"
                ExternalId    = c.StringId ?? c.Id.ToString(), // IMDb "tt..." else the numeric id as string
                Title         = c.Title,
                OriginalTitle = c.OriginalTitle,
                Year          = c.Year,
                PosterUrl     = c.PosterUrl,
                Overview      = c.Overview,
                Confidence    = c.Score,
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
            await FillThemeMusicAsync(item, folder, forceRefresh, log, ct);

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

        // Warm per-episode metadata so the detail page's detailed-list view is
        // instant on first open instead of lazy-fetching there. Best-effort: a
        // TMDB hiccup must never fail identification. TMDB-backed series only
        // (movies have no episode list; MAL-only anime have no TMDB season
        // detail). The /episodes endpoint stays the lazy fallback for everything
        // this skips — manual re-picks, transient misses, language changes.
        if (identified
            && item.TmdbId is int
            && item.MediaType is MediaItemType.Series or MediaItemType.Anime or MediaItemType.Multserials)
        {
            try
            {
                var epLang = (await appConfig.GetAsync(AppConfigKeys.MetadataLanguage, ct) ?? "en")
                    .Trim().ToLowerInvariant();
                var epRows = await EpisodeMetadataFetcher.FetchAsync(tmdb, item, epLang, ct, logger);
                if (epRows.Count > 0)
                {
                    var old = await db.EpisodeMetadata.Where(e => e.MediaItemId == item.Id).ToListAsync(ct);
                    if (old.Count > 0) { db.EpisodeMetadata.RemoveRange(old); await db.SaveChangesAsync(ct); }
                    db.EpisodeMetadata.AddRange(epRows);
                    await db.SaveChangesAsync(ct);
                    log?.Invoke($"[Episodes] Warmed {epRows.Count} episode(s) of metadata.");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Episode-metadata warm failed for {Id}", item.Id);
            }
        }

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
            await FillThemeMusicAsync(item, folder, true, null, ct);
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
        // tmdb_* / mal arrive here as a NUMERIC STRING — the /resolve endpoint
        // always calls this string overload (ResolveCandidateRequest.ExternalId
        // is a string). Previously only imdb/tvdb were handled here, so clicking
        // "Apply" on the TMDB or MAL field silently did nothing (the int overload
        // that handles tmdb/mal is never reached from the UI). Parse + delegate.
        if ((source is "tmdb_tv" or "tmdb_movie" or "mal") && int.TryParse(externalId, out var numId))
        {
            ok = source switch
            {
                "tmdb_tv"    => await PopulateTvFromTmdbAsync(item, folder, numId, true, null, ct),
                "tmdb_movie" => await PopulateMovieFromTmdbAsync(item, folder, numId, true, null, ct),
                "mal"        => await PopulateFromMalAsync(item, folder, numId, null, ct),
                _            => false,
            };
            // tv <-> movie fallback (mirror of the int overload): a pasted TMDB id
            // often doesn't tell whether the title is filed as TV or Movie.
            if (!ok && source == "tmdb_tv")         ok = await PopulateMovieFromTmdbAsync(item, folder, numId, true, null, ct);
            else if (!ok && source == "tmdb_movie") ok = await PopulateTvFromTmdbAsync(item, folder, numId, true, null, ct);
        }
        else if (source is "imdb_tv" or "imdb_movie" or "tvdb_tv")
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
            await FillThemeMusicAsync(item, folder, true, null, ct);
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
    /// <summary>
    /// Builds a human-readable error message for a failed TMDB manual lookup.
    /// Distinguishes between: missing/invalid API key, ID exists but wrong media type, ID doesn't exist.
    /// </summary>
    private async Task<string> BuildTmdbErrorHintAsync(string idStr, string source, CancellationToken ct)
    {
        var apiKey = await appConfig.GetTmdbApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return $"ID «{idStr}»: TMDB API key is not configured (go to Settings → API Keys).";

        // The key exists but the call failed. Give a hint about wrong source.
        var otherSource = source == "tmdb_tv" ? "tmdb_movie" : "tmdb_tv";
        var otherLabel  = source == "tmdb_tv" ? "a Movie" : "a TV Series";
        return $"ID «{idStr}» was not found as {(source == "tmdb_tv" ? "a TV Series" : "a Movie")} on TMDB. " +
               $"If this is {otherLabel}, switch the source dropdown to \"{otherSource}\" and try again.";
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

    // ── Re-localisation (text + poster only) ──────────────────────────────────

    /// <summary>
    /// Lightweight re-localisation of one already-identified TMDB item: re-fetches the
    /// main TV/Movie detail in <paramref name="lang"/> and overwrites ONLY the
    /// language-dependent fields — Title, Description, Genres — plus the poster when a
    /// poster localized to that language exists (otherwise the current poster is kept,
    /// never downgraded). Matched ids, ratings, episode mappings, original/CJK/English
    /// titles and the audio Language are left untouched. Empty overview/title fall back
    /// to English per field. Mutates <paramref name="item"/> in place; the caller owns
    /// persistence. Returns false for non-TMDB items (e.g. MAL-only anime) or when the
    /// detail fetch fails. Used by <see cref="MetadataLanguageService"/>'s library pass.
    /// </summary>
    public async Task<bool> RelocalizeItemAsync(MediaItem item, string lang, CancellationToken ct = default)
    {
        if (item.TmdbId is not { } tmdbId) return false;   // only TMDB-sourced items can be localized
        lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;

        string? title, overview, localizedPosterPath;
        List<string> genres, localizedGenres;

        if (item.MediaType == MediaItemType.Movie)
        {
            var d = await tmdb.GetMovieDetailAsync(tmdbId, lang, ct);
            if (d is null) return false;
            title = d.Title; overview = d.Overview;
            genres = TmdbGenreCatalog.English(d.Genres);   // canonical (English) — catalog logic depends on it
            localizedGenres = d.Genres.Select(g => g.Name).ToList();
            localizedPosterPath = d.Images?.Posters?.FirstOrDefault(p => p.Iso6391 == lang)?.FilePath;
            if (lang != "en" && string.IsNullOrWhiteSpace(overview))
            {
                var en = await tmdb.GetMovieDetailAsync(tmdbId, "en", ct);
                if (string.IsNullOrWhiteSpace(overview)) overview = en?.Overview;
                if (string.IsNullOrWhiteSpace(title))    title    = en?.Title;
            }
        }
        else
        {
            var d = await tmdb.GetTvDetailAsync(tmdbId, lang, ct);
            if (d is null) return false;
            title = d.Name; overview = d.Overview;
            genres = TmdbGenreCatalog.English(d.Genres);   // canonical (English) — catalog logic depends on it
            localizedGenres = d.Genres.Select(g => g.Name).ToList();
            localizedPosterPath = d.Images?.Posters?.FirstOrDefault(p => p.Iso6391 == lang)?.FilePath;
            if (lang != "en" && string.IsNullOrWhiteSpace(overview))
            {
                var en = await tmdb.GetTvDetailAsync(tmdbId, "en", ct);
                if (string.IsNullOrWhiteSpace(overview)) overview = en?.Overview;
                if (string.IsNullOrWhiteSpace(title))    title    = en?.Name;
            }
        }

        if (!string.IsNullOrWhiteSpace(title)) item.Title = title;
        item.Description = overview;
        if (genres.Count > 0)
            item.GenresJson = JsonSerializer.Serialize(genres, _json);
        // Localized genres for display (max localization); null → UI falls back to English canonical.
        item.GenresLocalizedJson = lang != "en" && localizedGenres.Count > 0
            ? JsonSerializer.Serialize(localizedGenres, _json)
            : null;

        // Swap the poster only when one localized to `lang` exists — overwrite the same
        // cached file (mirrors ApplySelectedImageAsync) and bump the cache-bust stamp.
        if (localizedPosterPath is { } lp && !string.IsNullOrEmpty(item.PosterPath))
            await tmdb.DownloadImageAsync(TmdbClient.PosterUrl(lp), item.PosterPath, ct);

        item.LastMetadataRefreshedAt = DateTime.UtcNow;
        return true;
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
