using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Recs;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Recommendation rails ("More like this" / "For you"), their dismissals,
/// and the per-user watchlist ("Хочу посмотреть"). All user-scoped.
/// </summary>
internal static class RecsEndpoints
{
    public static IEndpointRouteBuilder MapRecsEndpoints(this IEndpointRouteBuilder app)
    {
        // ── rails ────────────────────────────────────────────────────────
        // Both rails are scored over the whole catalog; folder-restricted roles
        // only get local cards for titles they can see (external TMDB cards stay).
        app.MapGet(ApiRoutes.MediaSimilar, async (
            Guid id, RecsService recs, IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var me = await userCtx.GetCurrentUserAsync(ct);
            var visible = await db.MediaItems.VisibleIdsAsync(me?.Role, ct);
            if (visible is not null && !visible.Contains(id)) return Results.NotFound();
            return Results.Ok(OnlyVisible(await recs.GetSimilarAsync(id, uid.Value, ct), visible));
        }).RequireAuthorization();

        app.MapGet(ApiRoutes.MeForYou, async (
            RecsService recs, IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var me = await userCtx.GetCurrentUserAsync(ct);
            var visible = await db.MediaItems.VisibleIdsAsync(me?.Role, ct);
            return Results.Ok(OnlyVisible(await recs.GetForYouAsync(uid.Value, ct), visible));
        }).RequireAuthorization();

        // ── "don't suggest" ──────────────────────────────────────────────
        app.MapPost(ApiRoutes.MeRecsDismiss, async (
            RecDismissRequest req, IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (req.MediaItemId is null && req.TmdbId is null)
                return Results.BadRequest(new { error = "MediaItemId or TmdbId required" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exists = await db.RecDismissals.AnyAsync(d => d.UserId == uid &&
                d.MediaItemId == req.MediaItemId && d.TmdbId == req.TmdbId, ct);
            if (!exists)
            {
                db.RecDismissals.Add(new RecDismissal
                {
                    Id = Guid.NewGuid(), UserId = uid.Value,
                    MediaItemId = req.MediaItemId, TmdbId = req.TmdbId,
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization();

        // ── watchlist ────────────────────────────────────────────────────
        app.MapGet(ApiRoutes.MeWatchlist, async (
            IUserContext userCtx, IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.WatchlistItems.AsNoTracking()
                .Where(w => w.UserId == uid)
                .OrderByDescending(w => w.CreatedAt)
                .ToListAsync(ct);
            // Local entries render live library data (title/poster may have
            // been re-identified since the add).
            var ids = rows.Where(r => r.MediaItemId != null).Select(r => r.MediaItemId!.Value).ToList();
            var me = await userCtx.GetCurrentUserAsync(ct);
            var items = await db.MediaItems.AsNoTracking()
                .ApplyRoleFolderFilter(me?.Role)
                .Where(m => ids.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, ct);
            return Results.Ok(rows.Select(r =>
            {
                if (r.MediaItemId is Guid mid && items.TryGetValue(mid, out var m))
                    return new WatchlistItemDto(r.Id, mid, m.TmdbId, MediaTitles.DisplayTitle(m), m.Year,
                        string.IsNullOrEmpty(m.PosterPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.PosterPath)}",
                        m.MediaType == Data.Models.MediaItemType.Movie ? "movie" : "tv", r.CreatedAt, r.AniListId);
                return new WatchlistItemDto(r.Id, r.MediaItemId, r.TmdbId, r.Title, r.Year,
                    r.PosterUrl, r.MediaType, r.CreatedAt, r.AniListId);
            }).ToArray());
        }).RequireAuthorization();

        app.MapPost(ApiRoutes.MeWatchlist, async (
            WatchlistAddRequest req, IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (req.MediaItemId is null && req.TmdbId is null && req.AniListId is null)
                return Results.BadRequest(new { error = "MediaItemId, TmdbId or AniListId required" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchlistItems.FirstOrDefaultAsync(w => w.UserId == uid &&
                (req.MediaItemId != null ? w.MediaItemId == req.MediaItemId
                 : req.TmdbId != null    ? w.TmdbId == req.TmdbId
                                         : w.AniListId == req.AniListId), ct);
            if (row is null)
            {
                row = new WatchlistItem
                {
                    Id          = Guid.NewGuid(),
                    UserId      = uid.Value,
                    MediaItemId = req.MediaItemId,
                    TmdbId      = req.TmdbId,
                    AniListId   = req.AniListId,
                    Title       = req.Title?.Trim() ?? "",
                    Year        = req.Year,
                    PosterUrl   = req.PosterUrl,
                    MediaType   = req.MediaType,
                };
                db.WatchlistItems.Add(row);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new WatchlistItemDto(row.Id, row.MediaItemId, row.TmdbId,
                row.Title, row.Year, row.PosterUrl, row.MediaType, row.CreatedAt, row.AniListId));
        }).RequireAuthorization();

        app.MapDelete(ApiRoutes.MeWatchlistById, async (
            Guid id, IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.WatchlistItems
                .Where(w => w.Id == id && w.UserId == uid)
                .ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static List<RecCardDto> OnlyVisible(List<RecCardDto> cards, HashSet<Guid>? visible)
        => visible is null
            ? cards
            : cards.Where(c => c.MediaItemId is not Guid mid || visible.Contains(mid)).ToList();
}
