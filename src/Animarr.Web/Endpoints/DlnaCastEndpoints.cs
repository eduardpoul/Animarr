using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// "Send to TV" — DLNA renderer discovery + control. The DLNA media server
/// (SSDP NOTIFY / ContentDirectory) endpoints live in Program.cs; this
/// wraps the cast (control-point) side only.
///
/// Signed-in only (<see cref="MediaAuth.Policies.MediaSession"/>): the web
/// player sends its cookie, the MAUI WebView player (behind the cookie-less
/// loopback proxy) appends its session media token. The play path is checked
/// by <see cref="MediaPathValidator"/> (library roots + the caller's role
/// folders), and the URL handed to the renderer — which can never sign in —
/// carries a link token signed for that one file and that user.
/// </summary>
internal static class DlnaCastEndpoints
{
    /// <summary>How long a cast URL stays playable: a long film plus a pause.
    /// TVs re-request with Range on every seek, so it must outlive the session.</summary>
    private static readonly TimeSpan CastLinkLifetime = TimeSpan.FromHours(8);

    public static IEndpointRouteBuilder MapDlnaCastEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.DlnaRenderers, async (
            DlnaCastService cast,
            CancellationToken ct) =>
        {
            var renderers = await cast.RefreshAsync(ct);
            var dtos = renderers
                .Select(r => new DlnaRendererDto(
                    Udn:           r.Udn,
                    FriendlyName:  r.FriendlyName,
                    ModelName:     r.ModelName ?? string.Empty,
                    Manufacturer:  r.Manufacturer ?? string.Empty,
                    ControlUrl:    r.AvTransportControlUrl,
                    LastSeenUtc:   r.LastSeen))
                .ToArray();
            return Results.Ok(dtos);
        }).RequireAuthorization(MediaAuth.Policies.MediaSession);

        app.MapPost(ApiRoutes.DlnaPlay, async (
            DlnaPlayRequest request,
            DlnaCastService cast,
            DlnaService dlna,
            MediaPathValidator pathValidator,
            MediaTokenService tokens,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RendererUdn))
                return Results.BadRequest("RendererUdn is required.");

            var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(request.FilePath, ct);
            if (!ok) return early!;
            if (userCtx.CurrentUserId is not Guid uid) return Results.Unauthorized();

            // Build the stream URL from the SAME origin the DLNA description
            // advertises. Using the inbound Request.Host header would break
            // when the user opens Animarr at https://hostname:8443 but the TV
            // (on the LAN) can't resolve that hostname — and would also point
            // at port 8443 (TLS), which TVs reject because of our self-signed
            // cert. The SSDP LOCATION header reaches all TVs at this same
            // origin already, so we know they can hit it.
            var baseUrl = dlna.AdvertisedHost ?? "http://localhost:8080";
            var ext  = Path.GetExtension(fullPath!).ToLowerInvariant();
            var mime = MediaMime.ForVideoExtension(ext);
            // Use /api/file for the cast URL — raw byte-stream with Range
            // support. TVs/AVRs decode the source natively (HEVC + AC3 +
            // Atmos passthrough and 5.1 surround intact, instead of
            // /api/video's stereo AAC downmix). Use the *validated* fullPath,
            // not request.FilePath — the URL pointed at the TV must be the
            // same canonical form /api/file will accept.
            // The renderer can't sign in: the URL carries a link token bound
            // to this file + this user (role folders keep applying on fetch).
            var streamUrl = MediaTokenService.AppendToUrl(
                $"{baseUrl}{ApiRoutes.File}?path={Uri.EscapeDataString(fullPath!)}",
                tokens.IssueLink(uid, fullPath!, CastLinkLifetime));
            var title = Path.GetFileNameWithoutExtension(fullPath!);

            var success = await cast.PlayAsync(request.RendererUdn, streamUrl, title, mime, ct);
            return success ? Results.NoContent() : Results.Problem("Renderer rejected the request.", statusCode: 502);
        }).RequireAuthorization(MediaAuth.Policies.MediaSession);

        return app;
    }
}
