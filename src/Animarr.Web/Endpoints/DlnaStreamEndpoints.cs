using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// <c>/api/dlna/stream</c> — the MPEG-TS remux a DLNA renderer gets instead of
/// the raw file when it needs another audio track, an external dub, or a
/// container / codec it can't take (see <see cref="DlnaStreamService"/>).
/// Renderers can't sign in: the URL carries a file-bound link token (cast) or
/// DLNA link token (MediaServer browse) for the VIDEO path; an external dub is
/// accepted only if it is one of that video's discovered external tracks.
/// </summary>
internal static class DlnaStreamEndpoints
{
    public static IEndpointRouteBuilder MapDlnaStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/dlna/stream", new[] { "GET", "HEAD" }, async (
            string path,
            int? audio,
            string? external,
            string? transcode,
            double? seek,
            int? offset,
            MediaPathValidator pathValidator,
            ExternalTrackService externalTracks,
            DlnaStreamService streams,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;

            string? externalFull = null;
            if (!string.IsNullOrWhiteSpace(external))
            {
                string wanted;
                try { wanted = Path.GetFullPath(external); }
                catch { return Results.BadRequest(); }
                var dub = (await externalTracks.FindForVideoAsync(fullPath!, probe: false, ct))
                    .FirstOrDefault(t => t.Kind == "audio" && string.Equals(Path.GetFullPath(t.Path), wanted, StringComparison.OrdinalIgnoreCase));
                if (dub is null) return Results.BadRequest("external is not an external audio track of this video.");
                externalFull = dub.Path;
            }

            // A renderer seeks by asking again with TimeSeekRange.dlna.org.
            var seekHeader = http.Request.Headers["TimeSeekRange.dlna.org"].ToString();
            var start = DlnaStreamService.ParseTimeSeekStart(seekHeader) ?? Math.Max(0, seek ?? 0);
            bool reencode = transcode == "1" || string.Equals(transcode, "true", StringComparison.OrdinalIgnoreCase);

            int offsetMs = Math.Clamp(offset ?? 0, -TrackPreferenceService.MaxAudioOffsetMs, TrackPreferenceService.MaxAudioOffsetMs);
            var (plan, error) = await streams.BuildAsync(fullPath!, audio ?? 0, externalFull, reencode, start, ct, offsetMs);
            if (plan is null) return Results.BadRequest(error);

            http.Response.ContentType = DlnaStreamService.ContentType;
            http.Response.Headers["transferMode.dlna.org"]   = "Streaming";
            http.Response.Headers["contentFeatures.dlna.org"] = DlnaStreamService.ProtocolFlags;
            http.Response.Headers.CacheControl = "no-store";
            if (!string.IsNullOrEmpty(seekHeader))
                http.Response.Headers["TimeSeekRange.dlna.org"] = DlnaStreamService.FormatNpt(start, plan.DurationSec);
            if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;

            if (!await streams.StreamAsync(plan, http.Response, ct))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Empty;
        })
        .WithName("DlnaStream")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }
}
