using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// "Optimize for TV" remux per title (<see cref="TvRemuxService"/>, in
/// place — no copies): status and start. <c>endsSeeding</c> names the
/// torrents a start stops for good (it rewrites files they seed). Admin-only (system settings
/// policy), like the setting that enables the feature
/// (<c>playback.tv_remux_enabled</c> through the AppConfig PUT).
/// </summary>
internal static class TvRemuxEndpoints
{
    public static IEndpointRouteBuilder MapTvRemuxEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/media/{mediaItemId:guid}/tv-remux")
            .RequireAuthorization(AuthConstants.Policies.SystemSettings);

        admin.MapGet("", async (Guid mediaItemId, TvRemuxService remux, CancellationToken ct) =>
        {
            var state = await remux.StatusAsync(mediaItemId, ct);
            return state is null
                ? Results.NotFound()
                : Results.Ok(new { enabled = await remux.IsEnabledAsync(ct), files = state.Files, endsSeeding = state.EndsSeeding });
        });

        admin.MapPost("", async (Guid mediaItemId, TvRemuxService remux, CancellationToken ct) =>
        {
            if (!await remux.IsEnabledAsync(ct))
                return Results.Problem(detail: "Enable playback.tv_remux_enabled first.", statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = "disabled" });
            var state = await remux.QueueAsync(mediaItemId, ct);
            return state is null
                ? Results.NotFound()
                : Results.Accepted(value: new { enabled = true, files = state.Files, endsSeeding = state.EndsSeeding });
        });

        return app;
    }
}
