using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Segments;
using Microsoft.EntityFrameworkCore;
using SharedEnums = Animarr.Shared;
using EfModels    = Animarr.Web.Data.Models;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for the media catalog — list, edit, refresh, resolve
/// candidate matches, plus continue-watching hints for MediaDetail.
///
/// Catalog list endpoint applies filtering by tag/folder/type/search at
/// SQL level and returns lean projections that the catalog grid renders
/// without further server round-trips.
/// </summary>
using Animarr.Web.Services.Auth;

internal static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Media, async (
            string? tag,
            string? search,
            SharedEnums.MediaItemType? type,
            Guid? folderId,
            string? category,
            Guid? categoryId,
            int? skip,
            int? take,
            string? sort,
            IDbContextFactory<AppDbContext> dbFactory,
            Animarr.Web.Services.Auth.IUserContext userCtx,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .AsQueryable();

            // v4 per-user filter — if the caller's role has a folder allowlist,
            // hide every MediaItem whose FolderId isn't in it. Built-in roles
            // (Master, User) leave FoldersJson empty → no filter.
            var me = await userCtx.GetCurrentUserAsync(ct);
            q = q.ApplyRoleFolderFilter(me?.Role);

            if (type is not null)
            {
                // Compare entity-enum to entity-enum. The old `(int)m.MediaType
                // == (int)type.Value` cast across two different MediaItemType
                // enums (entity vs Shared) — EF Core 10 fails to sanitise the
                // parameter for the Shared enum → 500 on every ?type= filter.
                var mt = (EfModels.MediaItemType)(int)type.Value;
                q = q.Where(m => m.MediaType == mt);
            }
            if (folderId is not null)
                q = q.Where(m => m.FolderId == folderId.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = $"%{search}%";
                q = q.Where(m =>
                    EF.Functions.Like(m.Title, s) ||
                    (m.OriginalTitle != null && EF.Functions.Like(m.OriginalTitle, s)) ||
                    (m.EnglishTitle  != null && EF.Functions.Like(m.EnglishTitle,  s)));
            }
            if (!string.IsNullOrWhiteSpace(tag))
            {
                q = q.Where(m => m.Tags.Any(t => t.MediaTag.Name == tag));
            }
            if (categoryId is not null)
            {
                q = q.Where(m => m.Categories.Any(c => c.CategoryId == categoryId.Value));
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                q = q.Where(m => m.Categories.Any(c => c.Category!.Name == category));
            }

            q = sort switch
            {
                "title"  => q.OrderBy(m => m.Title),
                "rating" => q.OrderByDescending(m => m.Rating ?? 0),
                "year"   => q.OrderByDescending(m => m.Year ?? 0),
                _        => q.OrderByDescending(m => m.CreatedAt),
            };

            if (skip is not null) q = q.Skip(skip.Value);
            if (take is not null) q = q.Take(take.Value);

            var rows = await q.ToListAsync(ct);
            // Per-user IsFavorite — one query for the whole list, then
            // O(1) HashSet lookups during projection.
            HashSet<Guid>? favIds = null;
            if (userCtx.CurrentUserId is Guid uid)
            {
                favIds = (await db.UserFavorites
                    .Where(f => f.UserId == uid)
                    .Select(f => f.MediaItemId)
                    .ToListAsync(ct)).ToHashSet();
            }
            return Results.Ok(rows.Select(r => r.ToDto(favIds)).ToArray());
        });

        app.MapGet(ApiRoutes.MediaById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (row is null) return Results.NotFound();
            HashSet<Guid>? favIds = null;
            if (userCtx.CurrentUserId is Guid uid)
            {
                var isFav = await db.UserFavorites
                    .AnyAsync(f => f.UserId == uid && f.MediaItemId == id, ct);
                if (isFav) favIds = new HashSet<Guid> { id };
            }
            return Results.Ok(row.ToDto(favIds));
        });

        app.MapPut(ApiRoutes.MediaById, async (
            Guid id,
            UpdateMediaItemRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            if (request.Title         is { } title)  entity.Title         = title;
            if (request.OriginalTitle is not null)   entity.OriginalTitle = request.OriginalTitle;
            if (request.CjkTitle      is not null)   entity.CjkTitle      = request.CjkTitle;
            if (request.EnglishTitle  is not null)   entity.EnglishTitle  = request.EnglishTitle;
            if (request.Year          is not null)   entity.Year          = request.Year;
            if (request.MediaType     is { } mt)     entity.MediaType     = (EfModels.MediaItemType)(int)mt;
            if (request.Description   is not null)   entity.Description   = request.Description;
            if (request.Tagline       is not null)   entity.Tagline       = request.Tagline;
            if (request.Genres        is not null)   entity.GenresJson    = MediaMappings.SerialiseStrings(request.Genres);
            if (request.Tags          is not null)   entity.TagsJson      = MediaMappings.SerialiseStrings(request.Tags);
            if (request.Status        is not null)   entity.Status        = request.Status;
            if (request.ContentRating is not null)   entity.ContentRating = request.ContentRating;
            if (request.Runtime       is not null)   entity.Runtime       = request.Runtime;
            if (request.Language      is not null)   entity.Language      = request.Language;
            if (request.Studio        is not null)   entity.Studio        = request.Studio;
            if (request.SeasonLabel   is not null)   entity.SeasonLabel   = request.SeasonLabel;
            if (request.Hue           is not null)   entity.Hue           = request.Hue;
            if (request.PosterPath    is not null)   entity.PosterPath    = request.PosterPath;
            if (request.FanartPath    is not null)   entity.FanartPath    = request.FanartPath;
            if (request.LogoPath      is not null)   entity.LogoPath      = request.LogoPath;
            entity.LastMetadataRefreshedAt = DateTime.UtcNow;

            // Replace tag associations atomically — easier than diffing.
            if (request.TagIds is not null)
            {
                entity.Tags.Clear();
                foreach (var tagId in request.TagIds.Distinct())
                {
                    entity.Tags.Add(new MediaItemTag { MediaTagId = tagId, MediaItemId = entity.Id });
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(entity.ToDto());
        });

        app.MapDelete(ApiRoutes.MediaById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();
            db.MediaItems.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapGet(ApiRoutes.MediaCandidates, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();
            return Results.Ok(entity.ToDto().Candidates);
        });

        // The metadata service's ApplyManualAsync needs the *folder* id, so
        // we look up MediaItem first to find the folder, then forward.
        app.MapPost(ApiRoutes.MediaResolve, async (
            Guid id,
            ResolveCandidateRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            MetadataService metadata,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            await metadata.ApplyManualAsync(entity.FolderId, request.Source, request.ExternalId, ct);

            // ApplyManualAsync updates the row via its own DbContext; reload
            // from a fresh context so we return the new state.
            await using var fresh = await dbFactory.CreateDbContextAsync(ct);
            var updated = await fresh.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            return updated is null ? Results.NoContent() : Results.Ok(updated.ToDto());
        });

        // "Refresh" routes through the identification queue with ForceRefresh=true
        // so we benefit from the same retry / log handling as auto-identify.
        app.MapPost(ApiRoutes.MediaRefresh, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            db.IdentificationQueues.Add(new IdentificationQueue
            {
                Id           = Guid.NewGuid(),
                FolderId     = entity.FolderId,
                Status       = EfModels.IdentificationQueueStatus.Queued,
                ForceRefresh = true,
                QueuedAt     = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return Results.Accepted();
        });

        // Poster / backdrop alternatives — projects the TMDB-image set fetched
        // by MetadataService into the candidate list the EditMetadataDrawer
        // renders. Each row is an ImageCandidateDto (Url + Width + Height) so
        // the picker can render a "1280x720" badge next to each thumbnail.
        app.MapGet(ApiRoutes.MediaPosterAlts, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            MetadataService metadata,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();
            var (posters, _, _) = await metadata.GetAvailableImagesAsync(entity.FolderId, ct);
            return Results.Ok(posters.ToArray());
        });
        app.MapGet(ApiRoutes.MediaBackdropAlts, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            MetadataService metadata,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();
            var (_, backdrops, _) = await metadata.GetAvailableImagesAsync(entity.FolderId, ct);
            return Results.Ok(backdrops.ToArray());
        });

        // Apply chosen poster / backdrop / logo. Downloads the image from the
        // candidate URL via TmdbClient.DownloadImageAsync, writes it to the
        // folder's MetaDir, and updates MediaItem.PosterPath / FanartPath /
        // LogoPath. Was missing on the API surface — the EditMetadataDrawer
        // POST got back a 400 with no body, surfacing as
        // "net_http_message_not_success_statuscode_reason" on the client.
        app.MapPost(ApiRoutes.MediaApplyImage, async (
            Guid id,
            ApplyImageRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            MetadataService metadata,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ImageType) ||
                string.IsNullOrWhiteSpace(request.Url))
                return Results.BadRequest(new { error = "ImageType and Url are required." });
            if (request.ImageType is not ("poster" or "fanart" or "logo"))
                return Results.BadRequest(new { error = $"Unknown ImageType '{request.ImageType}'. Use poster, fanart, or logo." });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            try
            {
                await metadata.ApplySelectedImageAsync(entity.FolderId, request.ImageType, request.Url, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ── Theme music ────────────────────────────────────────────────────────
        // Serves the cached anime OP/ED theme for the detail page's soft autoplay.
        // Lazy: on a cache miss for an anime-like item it fetches from AnimeThemes
        // on the fly (first hit is slow, then cached next to the media in .animarr/),
        // which backfills the existing library without a full re-identify. 404 when
        // the title has no theme. Range-enabled for seeking; AllowAnonymous so the
        // <audio> element loads cross-origin under MAUI like /api/image does. The
        // file path comes from the DB (not the client) so there's no path-injection.
        app.MapGet(ApiRoutes.MediaTheme, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            MetadataService metadata,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null) return Results.NotFound();

            var path = item.ThemePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = await metadata.EnsureThemeMusicAsync(id, ct);   // lazy backfill

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Results.NotFound();

            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ogg" or ".oga" => "audio/ogg",
                ".mp3"           => "audio/mpeg",
                ".m4a" or ".aac" => "audio/mp4",
                ".webm"          => "audio/webm",
                ".opus"          => "audio/opus",
                _                => "application/octet-stream",
            };
            return Results.File(path, mime, enableRangeProcessing: true);
        })
        .WithName("GetMediaTheme")
        .AllowAnonymous();

        // Re-fetch the theme from AnimeThemes (Edit Metadata → THEME MUSIC → Rescan).
        // Forces a fresh lookup and bypasses the global enabled-gate (explicit action).
        app.MapPost(ApiRoutes.MediaThemeRefresh, async (
            Guid id, MetadataService metadata, CancellationToken ct) =>
        {
            var path = await metadata.RefreshThemeMusicAsync(id, ct);
            return path is null
                ? Results.NotFound(new { error = "No theme found for this title on AnimeThemes." })
                : Results.NoContent();
        });

        // Manual override (THEME MUSIC → Add): download a user-supplied direct audio URL.
        app.MapPost(ApiRoutes.MediaThemeManual, async (
            Guid id, string? url, MetadataService metadata, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(url))
                return Results.BadRequest(new { error = "A 'url' query parameter is required." });
            var path = await metadata.SetThemeFromUrlAsync(id, url, ct);
            return path is null
                ? Results.BadRequest(new { error = "Couldn't download audio from that URL." })
                : Results.NoContent();
        });

        app.MapGet(ApiRoutes.MediaBackdropList, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var paths = await db.MediaItems
                .Where(m => m.FanartPath != null)
                .OrderByDescending(m => m.Popularity ?? 0)
                .Take(20)
                .Select(m => m.FanartPath!)
                .ToListAsync(ct);
            return Results.Ok(paths.ToArray());
        });

        app.MapGet(ApiRoutes.MediaNeedsReview, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.MediaItems
                .Include(m => m.Tags)
                .Include(m => m.Categories).ThenInclude(c => c.Category)
                .Where(m => (int)m.IdentificationStatus == (int)SharedEnums.IdentificationStatus.NeedsReview)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        // File enumeration — surfaces the (season, episode) → file mapping the
        // Razor MediaDetail page computes locally. WASM/MAUI consumers use this
        // to drive the per-episode Play CTA without filesystem access.
        app.MapGet(ApiRoutes.MediaFiles, async (
            Guid id,
            MediaFileResolver resolver,
            CancellationToken ct) =>
        {
            var files = await resolver.ResolveAsync(id, ct);
            return Results.Ok(files);
        });

        // Tier 2 — manual (season, episode) override for one file. Upserts a
        // "manual" row the resolver overlays on top of its deterministic parse;
        // manual is never clobbered by re-scans. Null season/episode keeps the
        // deterministic value for that field.
        app.MapPut(ApiRoutes.MediaFileMapping, async (
            Guid id,
            EpisodeMappingRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.FilePath))
                return Results.BadRequest("FilePath is required.");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.MediaItems.AnyAsync(m => m.Id == id, ct))
                return Results.NotFound();

            var row = await db.EpisodeFileMappings
                .FirstOrDefaultAsync(m => m.MediaItemId == id && m.FilePath == req.FilePath, ct);
            if (row is null)
            {
                row = new EfModels.EpisodeFileMapping { MediaItemId = id, FilePath = req.FilePath };
                db.EpisodeFileMappings.Add(row);
            }
            row.Season     = req.Season;
            row.Episode    = req.Episode;
            row.Source     = EfModels.MappingSource.Manual;
            row.Confidence = 1.0;
            row.UpdatedAt  = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Clear a stored override (?path=…) — reverts that file to the
        // deterministic parse. No-op if no row exists.
        app.MapDelete(ApiRoutes.MediaFileMapping, async (
            Guid id,
            string path,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.EpisodeFileMappings
                .Where(m => m.MediaItemId == id && m.FilePath == path)
                .ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // Tier 1 — ask the LLM to place files the deterministic parser left
        // unmatched. Stores Source="llm" overrides; returns the count placed
        // (0 when the LLM is disabled or unreachable — never an error).
        app.MapPost(ApiRoutes.MediaResolveEpisodes, async (
            Guid id,
            EpisodeLlmResolver llmResolver,
            CancellationToken ct) =>
        {
            var placed = await llmResolver.ResolveAsync(id, ct);
            return Results.Ok(placed);
        });

        // Compute per-season absolute offsets from TMDB air-date gaps (donghua:
        // TMDB one season, disk split). Stores MediaItem.SeasonOffsetsJson;
        // returns the diskSeason→offset map ({} when not applicable).
        app.MapPost(ApiRoutes.MediaResolveSeasons, async (
            Guid id,
            SeasonOffsetResolver seasonResolver,
            CancellationToken ct) =>
        {
            var offsets = await seasonResolver.ResolveAsync(id, ct);
            return Results.Ok(offsets ?? new Dictionary<int, int>());
        });

        // Continue-watching hint for the MediaDetail hero CTA. Resolution order:
        //   1) Resume the most-recently-touched in-progress episode/movie.
        //   2) Series/anime with no in-progress row → the next UNWATCHED episode
        //      on disk after the highest watched one (so "watched 1..228, only
        //      229 left" yields "Continue · EP 229", not "Play first episode").
        //   3) Nothing watched → play first; everything watched → rewatch.
        // User-scoped: only the current user's watch state counts.
        app.MapGet(ApiRoutes.MediaContinue, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            MediaFileResolver resolver,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            var uid = userCtx.CurrentUserId;
            var states = await db.WatchStates
                .Where(w => w.MediaItemId == id && (uid == null || w.UserId == uid))
                .OrderByDescending(w => w.LastSeenAt)
                .ToListAsync(ct);

            // 1) Resume an in-progress row (mid-episode / mid-movie).
            var resume = states.FirstOrDefault(w => !w.IsWatched && w.ProgressMs is > 0);
            if (resume is not null)
            {
                var label = resume.Episode is null ? "Continue" : $"Continue · EP {resume.Episode:D2}";
                return Results.Ok(new ContinueWatchDto(
                    Kind: "continue", Label: label, MediaItemId: id,
                    Season: resume.Season, Episode: resume.Episode, FilePath: resume.FilePath,
                    ProgressMs: resume.ProgressMs, RuntimeMs: resume.RuntimeMs));
            }

            var watchedAny = states.Any(w => w.IsWatched);

            // Movies have no episode layout: watched → rewatch, else → play.
            if (entity.MediaType == EfModels.MediaItemType.Movie)
            {
                return Results.Ok(new ContinueWatchDto(
                    Kind: watchedAny ? "rewatch" : "first",
                    Label: watchedAny ? "Rewatch from start" : "Play movie",
                    MediaItemId: id, Season: null, Episode: null, FilePath: null,
                    ProgressMs: null, RuntimeMs: null));
            }

            // 2) Series/anime — find the next unwatched episode on disk.
            var watchedKeys = states
                .Where(w => w.IsWatched && w.Episode != null)
                .Select(w => (Season: w.Season ?? 1, Episode: w.Episode!.Value))
                .ToHashSet();

            MediaFileDto[] files;
            try { files = await resolver.ResolveAsync(id, ct); }
            catch { files = Array.Empty<MediaFileDto>(); }
            var onDisk = files
                .Where(f => f.Episode is not null)
                .Select(f => (Season: f.Season ?? 1, Episode: f.Episode!.Value))
                .Distinct()
                .OrderBy(x => x.Season).ThenBy(x => x.Episode)
                .ToList();

            (int Season, int Episode)? target = null;
            if (onDisk.Count > 0)
            {
                if (watchedKeys.Count > 0)
                {
                    var maxW = watchedKeys.OrderByDescending(x => x.Season).ThenByDescending(x => x.Episode).First();
                    // First on-disk episode after the highest watched one that's unwatched…
                    target = onDisk
                        .Where(k => (k.Season > maxW.Season || (k.Season == maxW.Season && k.Episode > maxW.Episode)) && !watchedKeys.Contains(k))
                        .Select(k => ((int Season, int Episode)?)k)
                        .FirstOrDefault();
                    // …else the earliest unwatched gap (skipped middle episodes).
                    target ??= onDisk
                        .Where(k => !watchedKeys.Contains(k))
                        .Select(k => ((int Season, int Episode)?)k)
                        .FirstOrDefault();
                }
                else
                {
                    // Nothing watched yet → first on-disk episode.
                    target = onDisk[0];
                }
            }

            if (target is { } t)
            {
                var file = files.FirstOrDefault(f => (f.Season ?? 1) == t.Season && f.Episode == t.Episode);
                var firstEver = watchedKeys.Count == 0;
                return Results.Ok(new ContinueWatchDto(
                    Kind:       firstEver ? "first" : "next",
                    Label:      firstEver ? "Play first episode" : $"Continue · EP {t.Episode:D2}",
                    MediaItemId: id,
                    Season:     t.Season,
                    Episode:    t.Episode,
                    FilePath:   file?.FilePath,
                    ProgressMs: null,
                    RuntimeMs:  null));
            }

            // 3) No unwatched episode on disk: rewatch if anything was watched,
            //    else a bare "play first" (no files resolved yet).
            return Results.Ok(new ContinueWatchDto(
                Kind:       watchedAny ? "rewatch" : "first",
                Label:      watchedAny ? "Play again from start" : "Play first episode",
                MediaItemId: id, Season: null, Episode: null, FilePath: null,
                ProgressMs: null, RuntimeMs: null));
        });

        // Per-episode thumbnail. Order: cache → TMDB episode still → ffmpeg frame
        // from the local video → the show's own art (so the card is never broken).
        // Lazy (the EpisodeCard <img loading="lazy">) + cached under the folder's
        // image-cache, so a 200-episode donghua stops repeating one season poster.
        app.MapGet(ApiRoutes.MediaEpisodeThumb, async (
            Guid id, int season, int episode,
            IDbContextFactory<AppDbContext> dbFactory,
            MediaFileResolver resolver,
            TmdbClient tmdb,
            MediaCachePaths cachePaths,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null) return Results.NotFound();

            static bool Ok(string p) => File.Exists(p) && new FileInfo(p).Length > 0;
            var cache = Path.Combine(cachePaths.ForFolder(item.FolderId), $"ep-s{season}e{episode}.jpg");
            if (Ok(cache))
                return Results.File(cache, "image/jpeg", enableRangeProcessing: true);

            var files = await resolver.ResolveAsync(id, ct);
            var f = files.FirstOrDefault(x => (x.Season ?? 1) == season && x.Episode == episode);

            // 1) TMDB episode still. For donghua absolute/re-split layouts the
            //    matching TMDB episode is the absolute one (single TMDB season).
            if (item.TmdbId is int tmdbId && tmdbId > 0)
            {
                var tmdbSeason = f?.AbsoluteEpisode is not null ? 1 : season;
                var tmdbEp     = f?.AbsoluteEpisode ?? episode;
                try
                {
                    var detail = await tmdb.GetSeasonDetailAsync(tmdbId, tmdbSeason, ct);
                    var still  = detail?.Episodes?.FirstOrDefault(e => e.EpisodeNumber == tmdbEp)?.StillPath;
                    if (!string.IsNullOrEmpty(still)
                        && await tmdb.DownloadImageAsync(TmdbClient.StillUrl(still!), cache, ct)
                        && Ok(cache))
                        return Results.File(cache, "image/jpeg", enableRangeProcessing: true);
                }
                catch { /* network/TMDB hiccup — fall through to ffmpeg */ }
            }

            // 2) ffmpeg frame from the on-disk video.
            if (f?.FilePath is string fp && File.Exists(fp)
                && await GrabVideoFrameAsync(fp, cache, ct) && Ok(cache))
                return Results.File(cache, "image/jpeg", enableRangeProcessing: true);

            // 3) Last resort: the show's own art, so the card is never broken.
            var fallback = item.FanartPath ?? item.PosterPath;
            if (!string.IsNullOrEmpty(fallback) && File.Exists(fallback))
                return Results.File(fallback, "image/jpeg", enableRangeProcessing: true);

            return Results.NotFound();
        });

        // ── Skip intro / credits segments ────────────────────────────────────
        // GET with ?season=&episode= returns one episode's segments and lazily
        // detects them via the cheap providers (AniSkip) on a first-watch miss,
        // so the player gets times immediately. Without params it returns every
        // segment already known for the item (no detection). AllowAnonymous to
        // match the other playback endpoints the player calls directly.
        app.MapGet(ApiRoutes.MediaSegments, async (
            Guid id,
            int? season,
            int? episode,
            IDbContextFactory<AppDbContext> dbFactory,
            MediaFileResolver resolver,
            SegmentDetectionService detector,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null) return Results.NotFound();

            if (season is int s && episode is int e)
            {
                var have = await db.EpisodeSegments.AsNoTracking()
                    .Where(x => x.MediaItemId == id && x.Season == s && x.Episode == e)
                    .ToListAsync(ct);

                // Lazy detect on a miss using cheap providers only (a single
                // AniSkip call). Best-effort — the player falls back to 95%.
                if (have.Count == 0 && item.MalId is > 0)
                {
                    try
                    {
                        var files = await resolver.ResolveAsync(id, ct);
                        var fp = files.FirstOrDefault(f => (f.Season ?? 1) == s && f.Episode == e)?.FilePath;
                        if (!string.IsNullOrEmpty(fp) &&
                            await detector.DetectForEpisodeAsync(item, s, e, fp!, cheapOnly: true, ct))
                        {
                            have = await db.EpisodeSegments.AsNoTracking()
                                .Where(x => x.MediaItemId == id && x.Season == s && x.Episode == e)
                                .ToListAsync(ct);
                        }
                    }
                    catch { /* best-effort lazy detection */ }
                }
                // Smart credits fallback: this episode has no detected credits →
                // use the median credits start of the season's other episodes, so
                // the next-episode card appears at the ending instead of at the
                // player's blunt 95%-of-runtime guess.
                double? creditsFallback = null;
                if (!have.Any(x => x.Kind == SegmentKind.Credits))
                {
                    var others = await db.EpisodeSegments.AsNoTracking()
                        .Where(x => x.MediaItemId == id && x.Season == s
                                 && x.Kind == SegmentKind.Credits && x.Episode != e)
                        .Select(x => x.StartSec)
                        .ToListAsync(ct);
                    if (others.Count > 0)
                    {
                        others.Sort();
                        creditsFallback = others[others.Count / 2];
                    }
                }
                return Results.Ok(ToSegmentsDto(s, e, have, creditsFallback));
            }

            var all = await db.EpisodeSegments.AsNoTracking()
                .Where(x => x.MediaItemId == id)
                .ToListAsync(ct);
            var dtos = all
                .GroupBy(x => (x.Season, x.Episode))
                .OrderBy(g => g.Key.Season).ThenBy(g => g.Key.Episode)
                .Select(g => ToSegmentsDto(g.Key.Season, g.Key.Episode, g.ToList()))
                .ToArray();
            return Results.Ok(dtos);
        })
        .AllowAnonymous();

        // Manual override — Source=Manual, never clobbered by detection. Null
        // start/end clears that kind's override.
        app.MapPut(ApiRoutes.MediaSegments, async (
            Guid id,
            SegmentOverrideRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            SegmentKind? kind = req.Kind?.ToLowerInvariant() switch
            {
                "intro"   => SegmentKind.Intro,
                "credits" => SegmentKind.Credits,
                "recap"   => SegmentKind.Recap,
                _         => null,
            };
            if (kind is null) return Results.BadRequest(new { error = "Kind must be intro, credits or recap." });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.MediaItems.AnyAsync(m => m.Id == id, ct)) return Results.NotFound();

            var row = await db.EpisodeSegments.FirstOrDefaultAsync(
                x => x.MediaItemId == id && x.Season == req.Season && x.Episode == req.Episode && x.Kind == kind.Value, ct);

            if (req.StartSec is null || req.EndSec is null)
            {
                if (row is not null) { db.EpisodeSegments.Remove(row); await db.SaveChangesAsync(ct); }
                return Results.NoContent();
            }
            if (req.EndSec <= req.StartSec)
                return Results.BadRequest(new { error = "EndSec must be greater than StartSec." });

            if (row is null)
            {
                row = new EfModels.EpisodeSegment
                {
                    MediaItemId = id, Season = req.Season, Episode = req.Episode, Kind = kind.Value,
                };
                db.EpisodeSegments.Add(row);
            }
            row.StartSec      = req.StartSec.Value;
            row.EndSec        = req.EndSec.Value;
            row.Source        = SegmentSource.Manual;
            row.DetectedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>Collapse the per-(season,episode) segment rows into the flat DTO
    /// the player consumes.</summary>
    private static EpisodeSegmentsDto ToSegmentsDto(int season, int episode, List<EfModels.EpisodeSegment> segs,
        double? creditsStartFallback = null)
    {
        EfModels.EpisodeSegment? Pick(SegmentKind k) => segs.FirstOrDefault(x => x.Kind == k);
        var intro   = Pick(SegmentKind.Intro);
        var credits = Pick(SegmentKind.Credits);
        var recap   = Pick(SegmentKind.Recap);
        // No detected credits → use the season-median fallback the caller passed,
        // so the next-episode card still lands at the ending (not at 95%).
        return new EpisodeSegmentsDto(
            season, episode,
            intro?.StartSec,   intro?.EndSec,
            credits?.StartSec ?? creditsStartFallback, credits?.EndSec,
            recap?.StartSec,   recap?.EndSec);
    }

    // Cap concurrent ffmpeg frame-grabs: opening a season fires a burst of lazy
    // <img> loads, and each cache miss would otherwise spawn its own encoder.
    private static readonly SemaphoreSlim _frameGrabGate = new(3);

    /// <summary>Best-effort: grab one ~480px-wide jpeg frame from <paramref name="file"/>
    /// into <paramref name="outPath"/>. Seeks 90s in (past the OP); retries near
    /// the start for very short clips.</summary>
    private static async Task<bool> GrabVideoFrameAsync(string file, string outPath, CancellationToken ct)
    {
        await _frameGrabGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            foreach (var ss in new[] { "90", "3" })
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        RedirectStandardError  = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    };
                    foreach (var a in new[] { "-nostdin", "-ss", ss, "-i", file,
                                              "-frames:v", "1", "-vf", "scale=480:-1",
                                              "-q:v", "4", "-y", outPath })
                        psi.ArgumentList.Add(a);

                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is null) continue;
                    await p.WaitForExitAsync(ct);
                    if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return true;
                }
                catch { /* try the next seek offset */ }
            }
            return false;
        }
        finally { _frameGrabGate.Release(); }
    }
}
