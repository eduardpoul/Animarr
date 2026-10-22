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

        // ─── /api/subtitle/srt — a text track as SRT (DLNA renderers) ─────────
        // What TVs take as a sidecar (sec:CaptionInfoEx / text/srt res). Same
        // cache, charset handling and bitmap refusal as /api/subtitle.
        app.MapGet("/api/subtitle/srt", async (
            string path,
            int? track,
            MediaPathValidator pathValidator,
            SubtitleTrackInfoService tracks,
            MediaCachePaths cachePaths,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            var index = track ?? 0;
            if (index < 0) return Results.BadRequest();
            try
            {
                var r = await SubtitleCache.GetOrExtractAsync(fullPath!, index, "srt", cachePaths.CacheRoot, ct,
                    codecLookup: c => tracks.CodecOfAsync(fullPath!, index, c),
                    subtitleCodecs: c => tracks.CodecsAsync(fullPath!, c));
                if (r.Path is null)
                {
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Problem(detail: "This subtitle track can't be converted to text.",
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?> { ["code"] = r.FailureCode ?? SubtitleCache.ConvertFailed });
                }
                http.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(r.Path, "text/srt; charset=utf-8");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("ApiSubtitleSrt").LogError(ex, "SRT extract failed for {Path} track {Track}", fullPath, index);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("GetSubtitleSrt")
        .RequireAuthorization(MediaAuth.Policies.Media);

        // ─── /api/subtitle/sup — a PGS track as raw .sup (for libbitsub) ──────
        // Embedded PGS: copied out once per file version (a full read of the
        // container) into the subtitle cache. A standalone .sup is served as is.
        // Anything else is a fast 422 with code "not_pgs" — no ffmpeg run.
        app.MapGet("/api/subtitle/sup", async (
            string path,
            int? track,
            MediaPathValidator pathValidator,
            SubtitleTrackInfoService tracks,
            MediaCachePaths cachePaths,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            var index = track ?? 0;
            if (index < 0) return Results.BadRequest();
            try
            {
                var r = await SubtitleCache.GetOrExtractSupAsync(fullPath!, index, cachePaths.CacheRoot,
                    c => tracks.CodecOfAsync(fullPath!, index, c), ct);
                if (r.Path is null)
                {
                    // ffmpeg's stderr names cache / temp paths: the log gets it,
                    // the client a code and a fixed sentence.
                    var code = r.FailureCode ?? SubtitleCache.ConvertFailed;
                    loggerFactory.CreateLogger("ApiSubtitleSup").LogWarning(
                        ".sup not available for {Path} track {Track} ({Code}, exit {Exit}): {Err}",
                        fullPath, index, code, r.ExitCode, r.Error.Length > 2000 ? r.Error[^2000..] : r.Error);
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Problem(
                        detail: code == SubtitleCache.NotPgs
                            ? "This subtitle track isn't a PGS track."
                            : "This subtitle track couldn't be extracted.",
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "Subtitle track not available as .sup",
                        extensions: new Dictionary<string, object?> { ["code"] = code });
                }
                http.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(r.Path, "application/octet-stream", enableRangeProcessing: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("ApiSubtitleSup").LogError(ex, ".sup extract failed for {Path} track {Track}", fullPath, index);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("GetSubtitleSup")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }
}
