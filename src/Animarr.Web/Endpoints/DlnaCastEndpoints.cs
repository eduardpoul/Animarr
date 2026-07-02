using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// "Send to TV" — DLNA renderer discovery + control. The DLNA media server
/// (SSDP NOTIFY / ContentDirectory) endpoints live in Program.cs; this
/// wraps the cast (control-point) side only.
///
/// AllowAnonymous: both endpoints are called by the player's JS overlay,
/// which in the MAUI apps runs behind a cookie-less WebView proxy — a
/// required-auth gate would break casting from the native apps. The play
/// path is validated against the library roots before anything is sent to
/// the renderer.
/// </summary>
internal static class DlnaCastEndpoints
{
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
        }).AllowAnonymous();

        app.MapPost(ApiRoutes.DlnaPlay, async (
            DlnaPlayRequest request,
            DlnaCastService cast,
            DlnaService dlna,
            MediaPathValidator pathValidator,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RendererUdn))
                return Results.BadRequest("RendererUdn is required.");

            var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(request.FilePath, ct);
            if (!ok) return early!;

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
            var streamUrl = $"{baseUrl}{ApiRoutes.File}?path={Uri.EscapeDataString(fullPath!)}";
            var title = Path.GetFileNameWithoutExtension(fullPath!);

            var success = await cast.PlayAsync(request.RendererUdn, streamUrl, title, mime, ct);
            return success ? Results.NoContent() : Results.Problem("Renderer rejected the request.", statusCode: 502);
        }).AllowAnonymous();

        return app;
    }
}
