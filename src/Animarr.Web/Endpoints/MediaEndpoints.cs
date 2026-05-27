using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
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
                q = q.Where(m => (int)m.MediaType == (int)type.Value);
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
        // by MetadataService into the URL list the EditMetadataDrawer renders.
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

        // Lightweight continue-watching hint built straight from WatchState
        // rows. The full Razor MediaDetail page also has a ContinueAction
        // built by ContinueResolver — but that resolver needs the on-disk
        // episode layout, which lives in the page. The API stays simple:
        // resume the most-recently-touched in-progress row.
        app.MapGet(ApiRoutes.MediaContinue, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null) return Results.NotFound();

            var states = await db.WatchStates
                .Where(w => w.MediaItemId == id)
                .OrderByDescending(w => w.LastSeenAt)
                .ToListAsync(ct);

            var resume = states.FirstOrDefault(w => !w.IsWatched && w.ProgressMs is > 0);
            if (resume is not null)
            {
                var label = resume.Episode is null
                    ? "Continue"
                    : $"Continue · EP {resume.Episode:D2}";
                return Results.Ok(new ContinueWatchDto(
                    Kind:       "continue",
                    Label:      label,
                    MediaItemId: id,
                    Season:     resume.Season,
                    Episode:    resume.Episode,
                    FilePath:   resume.FilePath,
                    ProgressMs: resume.ProgressMs,
                    RuntimeMs:  resume.RuntimeMs));
            }

            // No in-progress row — fall back to "play first" hint.
            return Results.Ok(new ContinueWatchDto(
                Kind:       "first",
                Label:      entity.MediaType == EfModels.MediaItemType.Movie ? "Play movie" : "Play first episode",
                MediaItemId: id,
                Season:     null,
                Episode:    null,
                FilePath:   null,
                ProgressMs: null,
                RuntimeMs:  null));
        });

        return app;
    }
}
