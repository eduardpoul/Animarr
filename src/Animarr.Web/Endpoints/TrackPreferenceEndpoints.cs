using Animarr.Shared.Models;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Per-profile track memory (playback plan §6.5, task 2.2). User-scoped like
/// the watch-state REST surface: every read and write is the signed-in
/// profile's own row. <c>?season=N</c> addresses a season row; without it the
/// title row (a movie, or every season of a series). Paths are validated the
/// way the media endpoints do it (library roots + the profile's folders).
/// The same resolution is reported by <c>/api/hls/start</c> as
/// <c>trackDefaults</c>.
/// </summary>
internal static class TrackPreferenceEndpoints
{
    public static IEndpointRouteBuilder MapTrackPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        // What a playback of this file would start with for this profile.
        app.MapGet("/api/me/track-prefs/resolve", async (
            string path,
            MediaPathValidator pathValidator,
            TrackPreferenceService prefs,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            return Results.Ok(await prefs.ResolveAsync(uid, fullPath!, ct));
        }).RequireAuthorization();

        app.MapGet("/api/me/track-prefs/{mediaItemId:guid}", async (
            Guid mediaItemId,
            int? season,
            TrackPreferenceService prefs,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (BadSeason(season) is { } bad) return bad;
            return Results.Ok(await prefs.GetAsync(uid.Value, mediaItemId, season, ct));
        }).RequireAuthorization();

        // Partial update — the client sends what it picked on which file
        // (embedded index / external path / off), the server stores the meaning.
        app.MapPut("/api/me/track-prefs/{mediaItemId:guid}", async (
            Guid mediaItemId,
            int? season,
            TrackPrefsUpdateRequest request,
            MediaPathValidator pathValidator,
            TrackPreferenceService prefs,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (BadSeason(season) is { } bad) return bad;
            // The profile's role decides which titles exist for it: one outside
            // its folders answers 404 like an unknown id (no existence oracle).
            var me = await userCtx.GetCurrentUserAsync(ct);
            if (me is null) return Results.Unauthorized();

            string? fullPath = null;
            if (!string.IsNullOrWhiteSpace(request.Path))
            {
                var (ok, full, earlyResult) = await pathValidator.ResolveLibraryFileAsync(request.Path, ct);
                if (!ok) return earlyResult!;
                fullPath = full;
            }
            var result = await prefs.UpdateAsync(uid.Value, me.Role, mediaItemId, season, request, fullPath, ct);
            return result.Prefs is { } dto
                ? Results.Ok(dto)
                : Results.Problem(detail: result.Error, statusCode: result.Status,
                    title: result.Status == StatusCodes.Status404NotFound ? "Not found" : "Invalid track preference");
        }).RequireAuthorization();

        app.MapDelete("/api/me/track-prefs/{mediaItemId:guid}", async (
            Guid mediaItemId,
            int? season,
            TrackPreferenceService prefs,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (BadSeason(season) is { } bad) return bad;
            await prefs.DeleteAsync(uid.Value, mediaItemId, season, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    /// <summary>400 for a <c>?season=</c> outside 0..<see cref="TrackPreferenceService.MaxSeason"/>.</summary>
    private static IResult? BadSeason(int? season)
        => season is < 0 or > TrackPreferenceService.MaxSeason
            ? Results.Problem(detail: $"season must be 0..{TrackPreferenceService.MaxSeason}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid track preference")
            : null;
}
