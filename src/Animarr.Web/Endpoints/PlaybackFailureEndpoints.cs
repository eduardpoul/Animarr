using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Decoder failure feedback (task 6.1). A player whose decoder fails on a
/// stream reports it here; PlaybackFailureService remembers it for the
/// reporting DEVICE (the HLS session owner's client id), and that device's
/// next start of the same kind of stream steps to the next plan
/// (<c>output.transcodeReasonCode: device_failed_before</c>, the start's
/// <c>candidates[]</c> show which tier was skipped). Same authorization as
/// the start: cookie or media token + MediaPathValidator.
///
///   POST   /api/playback/failure  {path, plan, videoCodec, audioCodec, error, track?}
///   GET    /api/playback/failure  → this device's remembered failures
///   DELETE /api/playback/failure[?path=X] → forget them (all, or this file's kind)
/// </summary>
internal static class PlaybackFailureEndpoints
{
    /// <param name="Path">The library file that was playing (the start's path).</param>
    /// <param name="Plan">output.plan of the failed stream ("directplay", "directstream",
    /// "ts-copy", "fmp4-copy", "nvenc-reencode", "vaapi-reencode", "sw-reencode").</param>
    /// <param name="VideoCodec">The codec the player was decoding (output.videoCodec).</param>
    /// <param name="AudioCodec">Likewise for the audio (output.audioCodec); required for track=audio.</param>
    /// <param name="Error">The player's error (hls.js details, a Media3 error code …), ≤ 300 chars kept.</param>
    /// <param name="Track">"video" (default) or "audio" — which decoder failed.</param>
    public sealed record FailureReport(string? Path, string? Plan, string? VideoCodec, string? AudioCodec,
        string? Error, string? Track);

    public static IEndpointRouteBuilder MapPlaybackFailureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/playback/failure", async (
            FailureReport body,
            MediaPathValidator pathValidator,
            PlaybackFailureService failures,
            HlsSessionService hls,
            DeviceTrust deviceTrust,
            HttpContext http) =>
        {
            var ct = http.RequestAborted;
            if (string.IsNullOrWhiteSpace(body.Path))
                return Problem(StatusCodes.Status400BadRequest, "missing_path", "path is required.");
            var plan = body.Plan?.Trim().ToLowerInvariant();
            if (!PlaybackFailureService.IsKnownPlan(plan))
                return Problem(StatusCodes.Status400BadRequest, "unknown_plan",
                    "plan must be output.plan of the failed stream (directplay, directstream, ts-copy, fmp4-copy, *-reencode).");
            var track = string.Equals(body.Track?.Trim(), "audio", StringComparison.OrdinalIgnoreCase)
                ? PlaybackFailureService.TrackAudio : PlaybackFailureService.TrackVideo;

            var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(body.Path, ct);
            if (!ok) return early!;
            var owner = await HlsEndpoints.SessionOwnerAsync(http, deviceTrust);

            string key;
            if (track == PlaybackFailureService.TrackAudio)
            {
                var codec = PlaybackFailureService.NormalizeAudioCodec(body.AudioCodec);
                if (codec is null)
                    return Problem(StatusCodes.Status400BadRequest, "unknown_audio_codec",
                        "track=audio needs the audioCodec that failed (aac, ac3, eac3, dts, truehd, opus, flac, mp3, vorbis, pcm).");
                key = "a:" + codec;
            }
            else if (await hls.DecodeSignatureAsync(fullPath!, ct) is { } sig) key = sig;
            else return Problem(StatusCodes.Status422UnprocessableEntity, "probe_failed", "The file could not be probed.");

            var entry = failures.Record(owner.ClientId, key, plan!, track, body.VideoCodec, body.AudioCodec, body.Error,
                http.Connection.RemoteIpAddress?.ToString());
            var steps = failures.StepsFor(owner.ClientId, track == PlaybackFailureService.TrackVideo ? key : null);
            return Results.Ok(new
            {
                recorded  = true,
                key,
                plan,
                track,
                count     = entry.Count,
                expiresAt = entry.LastAt + PlaybackFailureService.Expiry,
                // What this device's next start of this kind of stream avoids.
                next = new
                {
                    noDirectPlay   = steps.NoDirectPlay,
                    noDirectStream = steps.NoDirectStream,
                    noCopy         = steps.NoCopy,
                    noHevcOutput   = steps.NoHevcOutput,
                    transcodeAudio = steps.TranscodeAudio.Order(StringComparer.Ordinal).ToArray(),
                },
            });
        })
        .WithName("ReportPlaybackFailure")
        .RequireAuthorization(MediaAuth.Policies.Media);

        app.MapGet("/api/playback/failure", async (
            PlaybackFailureService failures, DeviceTrust deviceTrust, HttpContext http) =>
        {
            var owner = await HlsEndpoints.SessionOwnerAsync(http, deviceTrust);
            return Results.Ok(failures.ForDevice(owner.ClientId).Select(e => new
            {
                e.Key, e.Plan, e.Track, e.VideoCodec, e.AudioCodec, e.Error, e.Count, e.FirstAt, e.LastAt,
                expiresAt = e.LastAt + PlaybackFailureService.Expiry,
            }));
        })
        .WithName("ListPlaybackFailures")
        .RequireAuthorization(MediaAuth.Policies.Media);

        app.MapDelete("/api/playback/failure", async (
            string? path, MediaPathValidator pathValidator, PlaybackFailureService failures, HlsSessionService hls,
            DeviceTrust deviceTrust, HttpContext http) =>
        {
            var owner = await HlsEndpoints.SessionOwnerAsync(http, deviceTrust);
            List<string>? keys = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(path, http.RequestAborted);
                if (!ok) return early!;
                keys = [];
                if (await hls.DecodeSignatureAsync(fullPath!, http.RequestAborted) is { } sig) keys.Add(sig);
            }
            return Results.Ok(new { removed = failures.Clear(owner.ClientId, keys) });
        })
        .WithName("ClearPlaybackFailures")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }

    private static IResult Problem(int status, string code, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: "Invalid failure report",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
