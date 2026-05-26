using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// "Send to TV" — DLNA renderer discovery + control. The DLNA media server
/// (SSDP NOTIFY / ContentDirectory) endpoints live in Program.cs; this
/// wraps the cast (control-point) side only.
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
        });

        app.MapPost(ApiRoutes.DlnaPlay, async (
            DlnaPlayRequest request,
            HttpContext http,
            DlnaCastService cast,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RendererUdn))
                return Results.BadRequest("RendererUdn is required.");
            if (string.IsNullOrWhiteSpace(request.FilePath))
                return Results.BadRequest("FilePath is required.");

            // Build the absolute stream URL the renderer can fetch from us.
            // Anchor it on whatever host the client used so reverse-proxy
            // setups (Caddy / Traefik) work without extra config.
            var scheme = http.Request.Scheme;
            var hostHeader = http.Request.Host.Value;
            var streamUrl = $"{scheme}://{hostHeader}{ApiRoutes.File}?path={Uri.EscapeDataString(request.FilePath)}";

            var ext  = Path.GetExtension(request.FilePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".mp4"  => "video/mp4",
                ".m4v"  => "video/x-m4v",
                ".mov"  => "video/quicktime",
                ".mkv"  => "video/x-matroska",
                ".webm" => "video/webm",
                ".avi"  => "video/x-msvideo",
                ".ts"   => "video/mp2t",
                ".m2ts" => "video/mp2t",
                _       => "application/octet-stream",
            };
            var title = Path.GetFileNameWithoutExtension(request.FilePath);

            var ok = await cast.PlayAsync(request.RendererUdn, streamUrl, title, mime, ct);
            return ok ? Results.Accepted() : Results.NotFound();
        });

        return app;
    }
}
