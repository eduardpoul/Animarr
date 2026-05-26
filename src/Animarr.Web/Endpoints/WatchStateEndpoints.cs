using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for per-episode / per-file watch state. Adapter over
/// <see cref="IWatchStateService"/> — its method names are tuned for
/// internal callers (MarkMovieAsync / MarkEpisodeAsync) so the HTTP layer
/// translates to/from the request DTOs and reads the row back to return
/// a populated WatchStateDto.
/// </summary>
internal static class WatchStateEndpoints
{
    public static IEndpointRouteBuilder MapWatchStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.WatchStatesForMedia, async (
            Guid mediaItemId,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.WatchStates
                .Where(w => w.MediaItemId == mediaItemId)
                .OrderByDescending(w => w.LastSeenAt)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapPost(ApiRoutes.WatchStateProgress, async (
            RecordProgressRequest request,
            IWatchStateService watchStates,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            // playedDeltaSec=0 — the API consumer doesn't track delta; the
            // server-side row's TotalWatchTimeSec stays in sync via the
            // player's "seekend" event-driven pings, not pure-positional ones.
            await watchStates.RecordProgressAsync(
                request.MediaItemId,
                request.Season,
                request.Episode,
                request.FilePath,
                request.ProgressMs,
                request.RuntimeMs,
                playedDeltaSec: 0,
                ct);

            var dto = await LookupAsync(dbFactory, request.MediaItemId, request.Season, request.Episode, ct);
            return dto is null ? Results.NoContent() : Results.Ok(dto);
        });

        app.MapPost(ApiRoutes.WatchStateToggle, async (
            ToggleWatchedRequest request,
            IWatchStateService watchStates,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (request.Season is null || request.Episode is null)
                await watchStates.MarkMovieAsync(request.MediaItemId, request.IsWatched, ct);
            else
                await watchStates.MarkEpisodeAsync(
                    request.MediaItemId,
                    request.Season.Value,
                    request.Episode.Value,
                    request.IsWatched,
                    request.FilePath,
                    ct);

            var dto = await LookupAsync(dbFactory, request.MediaItemId, request.Season, request.Episode, ct);
            return dto is null ? Results.NoContent() : Results.Ok(dto);
        });

        app.MapPost(ApiRoutes.WatchStateReset, async (
            ResetProgressRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchStates.FirstOrDefaultAsync(w =>
                w.MediaItemId == request.MediaItemId &&
                w.Season      == request.Season      &&
                w.Episode     == request.Episode, ct);
            if (row is null) return Results.NoContent();

            row.ProgressMs = 0;
            row.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;

        static async Task<Animarr.Shared.Models.WatchStateDto?> LookupAsync(
            IDbContextFactory<AppDbContext> dbFactory,
            Guid mediaItemId,
            int? season,
            int? episode,
            CancellationToken ct)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.WatchStates.FirstOrDefaultAsync(w =>
                w.MediaItemId == mediaItemId &&
                w.Season      == season      &&
                w.Episode     == episode, ct);
            return row?.ToDto();
        }
    }
}
