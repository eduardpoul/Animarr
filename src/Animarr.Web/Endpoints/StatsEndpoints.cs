using Animarr.Shared;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Stats;

namespace Animarr.Web.Endpoints;

/// <summary>Personal watch statistics — one GET, user-scoped.</summary>
internal static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.MeStats, async (
            StatsService stats, IUserContext userCtx, CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            return Results.Ok(await stats.BuildAsync(uid.Value, ct));
        }).RequireAuthorization();

        return app;
    }
}
