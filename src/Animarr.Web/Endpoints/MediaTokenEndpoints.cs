using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// <c>GET /api/media-token</c> — hands a signed-in client its session media
/// token (see <see cref="MediaTokenService"/>). Cookie only (fallback policy):
/// a media token can't mint another media token.
///
/// Who asks: the MAUI app (its WebView's &lt;img&gt;/&lt;video&gt; point at
/// the remote server cross-origin, where the SameSite cookie isn't sent; its
/// TV pages load posters with the native image loader and play with ExoPlayer;
/// its SignalR connections run on a separate HTTP stack). The same-origin web
/// client never needs one — its cookie rides every request.
/// </summary>
internal static class MediaTokenEndpoints
{
    public static IEndpointRouteBuilder MapMediaTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.MediaToken, (IUserContext userCtx, MediaTokenService tokens, HttpContext http) =>
        {
            if (userCtx.CurrentUserId is not Guid uid) return Results.Unauthorized();
            var token = tokens.IssueSession(uid, out var expires);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new MediaTokenDto(token, expires, uid));
        });

        return app;
    }
}
