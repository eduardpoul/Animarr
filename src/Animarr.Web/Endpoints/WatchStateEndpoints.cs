using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for per-episode / per-file watch state.
///
/// v4 scoping: every read filters by <c>UserId == currentUser.Id</c> and every
/// write stamps the current user's id on the row. Orphan rows (created by
/// pre-v4 migrations or the external mpv tracker, which is anonymous) carry
/// <c>UserId == null</c> and are invisible to all authenticated users.
/// </summary>
internal static class WatchStateEndpoints
{
    public static IEndpointRouteBuilder MapWatchStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.WatchStatesForMedia, async (
            Guid mediaItemId,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.WatchStates
                .Where(w => w.MediaItemId == mediaItemId && w.UserId == uid)
                .OrderByDescending(w => w.LastSeenAt)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        }).RequireAuthorization();

        app.MapPost(ApiRoutes.WatchStateProgress, async (
            RecordProgressRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchStates.FirstOrDefaultAsync(w =>
                w.UserId      == uid &&
                w.MediaItemId == request.MediaItemId &&
                w.Season      == request.Season &&
                w.Episode     == request.Episode, ct);
            if (row is null)
            {
                row = new WatchState
                {
                    Id          = Guid.NewGuid(),
                    UserId      = uid,
                    MediaItemId = request.MediaItemId,
                    Season      = request.Season,
                    Episode     = request.Episode,
                    FilePath    = request.FilePath,
                    CreatedAt   = DateTime.UtcNow,
                    PlayCount   = 1,
                };
                db.WatchStates.Add(row);
            }
            row.ProgressMs = request.ProgressMs;
            if (request.RuntimeMs is > 0) row.RuntimeMs = request.RuntimeMs;
            if (!string.IsNullOrEmpty(request.FilePath)) row.FilePath = request.FilePath;
            row.LastSeenAt = DateTime.UtcNow;

            // Auto-flip IsWatched at ≥90% of runtime per CHANGELOG §1.
            if (row.ProgressMs is > 0 && row.RuntimeMs is > 0 &&
                (double)row.ProgressMs.Value / row.RuntimeMs.Value >= 0.9)
            {
                row.IsWatched = true;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(row.ToDto());
        }).RequireAuthorization();

        app.MapPost(ApiRoutes.WatchStateToggle, async (
            ToggleWatchedRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchStates.FirstOrDefaultAsync(w =>
                w.UserId      == uid &&
                w.MediaItemId == request.MediaItemId &&
                w.Season      == request.Season &&
                w.Episode     == request.Episode, ct);
            if (row is null)
            {
                row = new WatchState
                {
                    Id          = Guid.NewGuid(),
                    UserId      = uid,
                    MediaItemId = request.MediaItemId,
                    Season      = request.Season,
                    Episode     = request.Episode,
                    FilePath    = request.FilePath,
                    CreatedAt   = DateTime.UtcNow,
                };
                db.WatchStates.Add(row);
            }
            row.IsWatched  = request.IsWatched;
            row.LastSeenAt = DateTime.UtcNow;
            // Marking as watched pins progress to runtime so the bar matches the chip.
            if (request.IsWatched && row.RuntimeMs is > 0) row.ProgressMs = row.RuntimeMs;
            // Marking unwatched clears progress so the next play starts fresh.
            else if (!request.IsWatched) row.ProgressMs = null;

            await db.SaveChangesAsync(ct);
            return Results.Ok(row.ToDto());
        }).RequireAuthorization();

        app.MapPost(ApiRoutes.WatchStateReset, async (
            ResetProgressRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchStates.FirstOrDefaultAsync(w =>
                w.UserId      == uid &&
                w.MediaItemId == request.MediaItemId &&
                w.Season      == request.Season &&
                w.Episode     == request.Episode, ct);
            if (row is null) return Results.NoContent();

            row.ProgressMs = 0;
            row.IsWatched  = false;
            row.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }
}
