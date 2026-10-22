using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Subtitle assets beyond the WebVTT / ASS text of <c>/api/subtitle</c>
/// (MediaStreamEndpoints): which renderer each track needs, and the fonts an
/// ASS track is typeset with (plan task 2.5). Same authorization as
/// <c>/api/subtitle</c> — cookie or media token + <see cref="MediaPathValidator"/>.
/// </summary>
internal static class SubtitleAssetEndpoints
{
    public static IEndpointRouteBuilder MapSubtitleAssetEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── /api/subtitle/tracks — renderer facts per subtitle track ─────────
        // analyze=1 copies the not-yet-cached ASS tracks out (one full read of
        // the container for all of them) so their analysis is filled; without
        // it an embedded ASS track not served before has ass = null.
        app.MapGet("/api/subtitle/tracks", async (
            string path,
            string? analyze,
            MediaPathValidator pathValidator,
            SubtitleTrackInfoService tracks,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            bool deep = analyze == "1" || string.Equals(analyze, "true", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(await tracks.ListAsync(fullPath!, deep, ct));
        })
        .WithName("GetSubtitleTracks")
        .RequireAuthorization(MediaAuth.Policies.Media);

        // ─── /api/subtitle/fonts — font attachments (for JASSUB) ──────────────
        app.MapGet("/api/subtitle/fonts", async (
            string path,
            MediaPathValidator pathValidator,
            SubtitleTrackInfoService tracks,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            return Results.Ok(await tracks.FontsAsync(fullPath!, ct));
        })
        .WithName("GetSubtitleFonts")
        .RequireAuthorization(MediaAuth.Policies.Media);

        // ─── /api/subtitle/font — one attached font file ──────────────────────
        app.MapGet("/api/subtitle/font", async (
            string path,
            int id,
            MediaPathValidator pathValidator,
            SubtitleFontService fonts,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            var font = await fonts.GetFontFileAsync(fullPath!, id, ct);
            if (font is not { } f) return Results.NotFound();
            // Same file version → same bytes (the cache key holds mtime + size).
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(f.Path, f.ContentType, enableRangeProcessing: true);
        })
        .WithName("GetSubtitleFont")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }
}
