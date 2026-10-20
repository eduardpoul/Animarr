using System.Collections.Concurrent;
using Animarr.Shared;
using Animarr.Web.Data;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Franchise;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Franchise watch-order rail for a title. Reads the stored graph; a
/// never-checked title lazily kicks a background BFS (debounced per process)
/// so the rail the user is actually looking at fills within seconds instead
/// of waiting for the backfill queue.
/// </summary>
internal static class FranchiseEndpoints
{
    private static readonly ConcurrentDictionary<Guid, byte> _lazyKicked = new();

    public static IEndpointRouteBuilder MapFranchiseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.MediaFranchise, async (
            Guid id,
            FranchiseService franchise,
            IDbContextFactory<AppDbContext> dbFactory,
            IServiceScopeFactory scopeFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var me = await userCtx.GetCurrentUserAsync(ct);
            var visible = await db.MediaItems.VisibleIdsAsync(me?.Role, ct);
            if (visible is not null && !visible.Contains(id)) return Results.NotFound();

            var dto = await franchise.GetFranchiseAsync(id, ct);
            if (dto is not null)
            {
                // Folder-restricted roles: members outside their scope render as
                // "not in library" instead of linking to a title they can't open.
                if (visible is not null)
                    dto = dto with
                    {
                        Cards = dto.Cards.Select(c => c.MediaItemId is Guid mid && !visible.Contains(mid)
                            ? c with { MediaItemId = null, InLibrary = false, Watched = false }
                            : c).ToList(),
                    };
                return Results.Ok(dto);
            }

            // Nothing stored — kick one background walk for this title. Id-less
            // titles are included: the walk bridges ids by title on the fly.
            var unchecked_ = await db.MediaItems.AsNoTracking()
                .AnyAsync(m => m.Id == id && m.LastRelationsCheckAt == null, ct);
            if (unchecked_ && _lazyKicked.TryAdd(id, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var svc = scope.ServiceProvider.GetRequiredService<FranchiseService>();
                        await svc.RefreshForItemAsync(id, CancellationToken.None);
                    }
                    catch { /* best-effort — the backfill queue retries */ }
                });
            }
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }
}
