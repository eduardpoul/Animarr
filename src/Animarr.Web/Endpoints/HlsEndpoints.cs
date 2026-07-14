using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// HLS streaming lifecycle: POST start (with Direct Play / Direct Stream
/// probing), segment + playlist serving, keepalive and stop. The diagnostic
/// session list + bulk-stop are admin-gated; the rest are AllowAnonymous and
/// path-validated because the browser player fetches them without a cookie.
/// </summary>
internal static class HlsEndpoints
{
    public static IEndpointRouteBuilder MapHlsEndpoints(this IEndpointRouteBuilder app)
    {
    // ─── /api/hls — HLS streaming for in-browser playback ─────────────────────
    // Lifecycle:
    //   POST /api/hls/start?path=X[&seek=N]    → { token, manifestUrl }
    //   GET  /api/hls/{token}/master.m3u8      → playlist
    //   GET  /api/hls/{token}/seg-NNNNN.{m4s|ts} → segment (extension per plan)
    //   POST /api/hls/keepalive?token=T        → bump idle timer
    //   DELETE /api/hls/{token}                → stop session + free /tmp
    //
    // HlsSessionService picks one of three plans per source: MPEG-TS stream-copy
    // for H.264 (most files; PCR-sync, no GPU), fMP4 + VAAPI re-encode for HEVC
    // 8-bit (tight sync via -bf 0), or fMP4 stream-copy fallback for HEVC 10-bit
    // HDR/DV. The endpoint above is plan-agnostic — the manifest tells the
    // player what to fetch and we just serve files from the session dir.
    app.MapPost("/api/hls/start", async (
            string path,
            MediaPathValidator pathValidator,
            double? seek,
            int? audioOffsetHwMs,
            int? audioOffsetSwMs,
            // 0 = first audio stream (historical default). Surfaced via the HUD's
            // Audio popup which restarts the session with a different index when
            // the user picks another track — there's no in-stream switching with
            // our single-stream transcode pipeline.
            int? audioTrackIndex,
            // Cap output height (1080 / 720 / …). 0 or absent = native resolution.
            // Below the source height it forces a re-encode/downscale. Surfaced via
            // the HUD's Quality popup, which restarts the session with a new cap.
            int? maxHeight,
            // Absolute path to an external sideload audio file (a dub track that
            // isn't muxed in the source). When set, the session muxes it as a
            // second ffmpeg input and Direct Play is skipped (we must transcode to
            // combine the foreign audio with the source video). Discovered by
            // /api/external-tracks; re-validated here against the library roots.
            string? externalAudio,
            // Set by the client (MediaSource.isTypeSupported) when the browser can
            // decode HEVC. Lets the server Direct-Stream HEVC (stream-copy, original
            // quality) instead of re-encoding it to H.264. Absent → re-encode.
            // NB: bound as string, not bool — the client sends "1", which minimal
            // API's bool binder rejects with a 400 (it only accepts true/false).
            string? clientHevc,
            // Same flag for 10-bit / Main10 HEVC — gates Direct Play of HDR10 MP4s
            // (so they play on a native <video> where RTX VSR / HDR can engage).
            string? clientHevc10,
            // Cap output bitrate in Mbps (player Bitrate menu). >0 forces a re-encode
            // at this bitrate; absent/0 = no cap.
            int? maxBitrate,
            HlsSessionService hls,
            ILoggerFactory loggerFactory) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        // Validate the external audio path the same way (must be inside a watched
        // root + exist). On failure we DON'T fail playback — we just drop the dub
        // and play the source's own audio, which is the least-surprising fallback
        // for a stale path.
        string? externalAudioFull = null;
        if (!string.IsNullOrWhiteSpace(externalAudio))
        {
            var (extOk, extFull, _) = await pathValidator.ResolveLibraryFileAsync(externalAudio);
            if (extOk) externalAudioFull = extFull;
            else loggerFactory.CreateLogger("ApiHls")
                     .LogWarning("HLS start: ignoring out-of-bounds external audio {Path}", externalAudio);
        }

        try
        {
            var seekSec = seek is > 0 ? seek.Value : 0d;

            // Direct Play probe: if the source file is already in a browser-
            // native format (MP4/H.264/AAC), skip HLS entirely and have the
            // player point at /api/file. Zero transcode, instant start, perfect
            // sync. This is Plex/Jellyfin's "Direct Play" tier — the first
            // choice the server makes before falling back to transcoding.
            // Skip the Direct Play probe entirely when an external dub is in play:
            // combining foreign audio with the source video requires the HLS mux
            // path, so a direct file URL would silently drop the dub.
            bool wantHevc   = clientHevc   == "1" || string.Equals(clientHevc,   "true", StringComparison.OrdinalIgnoreCase);
            bool wantHevc10 = clientHevc10 == "1" || string.Equals(clientHevc10, "true", StringComparison.OrdinalIgnoreCase);
            // A height/bitrate cap (the HUD's Quality menu) forces a re-encode —
            // you can't shrink a stream-copy — so it must bypass BOTH native paths
            // (Direct Play AND Direct Stream) and fall through to the HLS pipeline
            // where maxHeight/maxBitrate actually apply. Same for an external dub,
            // which needs the HLS mux to combine foreign audio with the video.
            bool wantCap = (maxHeight ?? 0) > 0 || (maxBitrate ?? 0) > 0;
            var decision = externalAudioFull is null && !wantCap
                ? await hls.ChoosePlaybackAsync(fullPath!, wantHevc, wantHevc10)
                : new HlsSessionService.PlaybackDecision(false, null, 0, null);
            if (decision.DirectPlay && decision.DirectUrl is not null)
            {
                return Results.Ok(new
                {
                    // Direct Play response: client checks for this field and
                    // routes around the HLS-specific setup (no session token,
                    // no keepalive, no audio-sync calibration).
                    directPlayUrl = decision.DirectUrl,
                    totalDuration = decision.DurationSec,
                    resumeSec     = seekSec,
                    // What the player actually receives — for the HUD plashka.
                    // Direct Play means source passes through unchanged, so HDR
                    // tags etc. are preserved here.
                    output        = decision.Output,
                });
            }

            if (decision.DirectStream && decision.DirectUrl is not null)
            {
                return Results.Ok(new
                {
                    // Direct Stream response: /api/video remuxes the source to
                    // progressive fMP4 on the fly (video copy + audio→AAC). The
                    // client plays it on a native <video> — no HLS session, no
                    // keepalive — and seeks by re-requesting at ?seek=N. Video
                    // bitstream + HDR pass through unchanged.
                    directStreamUrl = decision.DirectUrl,
                    totalDuration   = decision.DurationSec,
                    resumeSec       = seekSec,
                    output          = decision.Output,
                });
            }

            // Per-client audio offsets — two channels, server picks which one to
            // apply based on the plan it chooses for this file:
            //   • HW = VAAPI re-encode path (HEVC 8-bit). Has near-zero baseline
            //     wobble thanks to -bf 0, but adds browser/decoder chain latency.
            //   • SW = fMP4 stream-copy path (HEVC 10-bit / HDR / DV). B-frames
            //     preserved → different residual wobble that needs its own value.
            // Both come from independent sliders in Settings; clamp to ±500ms so
            // a runaway client can't push audio half a second out of sync.
            // Clamp envelopes are asymmetric:
            //   HW (VAAPI re-encode) — ±500ms is plenty, decoder chain latency
            //     varies but never lands in three-digit-positive territory once
            //     -bf 0 strips B-frames.
            //   SW (fMP4 stream-copy) — UHD BluRay remuxes with B15 GOP structures
            //     produce ~625ms of audio-ahead at 23.976fps, so we allow up to
            //     +800ms. Going lower than -200ms makes no physical sense (audio
            //     would have to play BEFORE the file's first frame), so the
            //     negative side stays tight.
            var audioOffsetHwSec = audioOffsetHwMs is int hwMs
                ? Math.Clamp(hwMs, -500, 500) / 1000.0
                : 0.07;  // default matches DEFAULT_OFFSET_MS in calibrate.js
            var audioOffsetSwSec = audioOffsetSwMs is int swMs
                ? Math.Clamp(swMs, -200, 800) / 1000.0
                : 0.0;   // SW path has no sensible default — user must dial it in
            var result = await hls.StartAsync(fullPath!, seekSec,
                audioOffsetHwSec: audioOffsetHwSec,
                audioOffsetSwSec: audioOffsetSwSec,
                audioTrackIndex:  Math.Max(0, audioTrackIndex ?? 0),
                maxHeight:        Math.Max(0, maxHeight ?? 0),
                externalAudioPath: externalAudioFull,
                clientHevc:       wantHevc,
                maxBitrate:       Math.Max(0, maxBitrate ?? 0));
            return Results.Ok(new
            {
                token        = result.Token,
                manifestUrl  = result.ManifestRelativeUrl,
                // Full file duration in seconds. The JS bridge uses this to
                // report progress against the real runtime, since the playlist's
                // own duration is shortened to (totalDur - resumeSec) and the
                // player only knows about the shortened timeline.
                totalDuration = result.TotalDurationSec,
                resumeSec    = seekSec,
                audioOffsetHwMs = (int)Math.Round(audioOffsetHwSec * 1000),
                audioOffsetSwMs = (int)Math.Round(audioOffsetSwSec * 1000),
                // What the player actually receives — drives the HUD plashka.
                // For re-encode plans this differs from /api/probe output (e.g.
                // HEVC 10-bit DV source → H.264 8-bit SDR here).
                output       = result.Output,
            });
        }
        catch (Exception ex)
        {
            var logger = loggerFactory.CreateLogger("ApiHls");
            logger.LogError(ex, "HLS start failed for {Path}", fullPath);
            return Results.Problem("Failed to start HLS session.", statusCode: 500);
        }
    })
    .WithName("StartHls")
    .AllowAnonymous();

    app.MapGet("/api/hls/{token}/{file}", async (
            string token,
            string file,
            HttpContext http,
            HlsSessionService hls) =>
    {
        // Token is a hex GUID; file must be a flat filename (defence-in-depth
        // against path traversal even though session dir is already isolated).
        if (token.Length is < 16 or > 64 || token.Any(c => !Uri.IsHexDigit(c)))
            return Results.NotFound();
        var bare = Path.GetFileName(file);
        if (string.IsNullOrEmpty(bare) || bare != file) return Results.NotFound();

        var dir = hls.GetSessionDir(token);
        if (dir is null) return Results.NotFound();

        var probePath = Path.Combine(dir, bare);
        // Defence-in-depth: GetFileName already strips path separators above,
        // but if the session dir somehow became a symlink (admin moved /tmp,
        // tmpfs trickery, etc.) the resolved path could escape. Verify the
        // canonical full path stays inside the session dir before serving.
        var canonicalProbe = Path.GetFullPath(probePath);
        var canonicalDir   = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!canonicalProbe.StartsWith(canonicalDir, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        // Wait for ffmpeg to catch up if the file hasn't landed yet — the player
        // requests segments according to our pre-written VOD playlist, so for
        // points beyond ffmpeg's current encoding position we wait up to 30s
        // (HlsSessionService.SegmentWaitTimeout). Past that we 503 so hls.js
        // retries with backoff instead of bailing the stream.
        var full = await hls.WaitForFileAsync(token, bare, http.RequestAborted);
        if (full is null) return Results.StatusCode(503);

        // Two categories with different caching/transfer semantics:
        //
        //   • Playlists (.m3u8): small text, MUTABLE while ffmpeg encodes. We
        //     read the whole file on each hit and return it wholesale — Range
        //     requests against a mutable file race against ffmpeg rewrites and
        //     produce spurious 416 (browser cached size N, file is now size M < N,
        //     Range bytes=N- becomes unsatisfiable). Cheap because playlists are
        //     a few hundred bytes.
        //
        //   • Segments (.m4s/.ts/init.mp4): big binary, IMMUTABLE once flushed.
        //     Range support is essential — players seek by byte-range into the
        //     middle of a segment when scrubbing.
        var isPlaylist = bare.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        if (isPlaylist)
        {
            var bytes = await File.ReadAllBytesAsync(full);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(bytes, "application/vnd.apple.mpegurl");
        }

        http.Response.Headers.CacheControl = "public, max-age=86400, immutable";
        // MIME type: fMP4 segments are application/iso.segment (a.k.a. video/iso.
        // segment), MPEG-TS segments are video/MP2T. Browsers ignore both for
        // hls.js-driven playback (Content-Type is set on the master playlist
        // instead) but smart TVs and Chromecast inspect segment Content-Type
        // before deciding whether to attempt Direct Play, so we set it correctly.
        var isTsSegment = bare.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
        var mime = isTsSegment ? "video/MP2T" : "video/iso.segment";
        return Results.File(full, mime, enableRangeProcessing: true);
    })
    .WithName("GetHlsFile")
    .AllowAnonymous();

    app.MapPost("/api/hls/keepalive", (string token, HlsSessionService hls) =>
            hls.Touch(token) ? Results.NoContent() : Results.NotFound())
        .WithName("HlsKeepalive")
        .AllowAnonymous();

    app.MapDelete("/api/hls/{token}", (string token, HlsSessionService hls) =>
    {
        hls.Stop(token);
        return Results.NoContent();
    })
    .WithName("StopHls")
    .AllowAnonymous();

    // Diagnostic: list every active session with ffmpeg state + segment progress.
    // Hit this when a player is stuck on a frame to see whether the backing
    // session is alive, crashed, or completed-partial. Admin-gated — the snapshot
    // exposes library file paths and lets the caller correlate who watches what.
    app.MapGet("/api/hls/sessions", (HlsSessionService hls) =>
        Results.Ok(hls.Snapshot()))
        .WithName("ListHlsSessions")
        .RequireAuthorization(AuthConstants.Policies.SystemSettings);

    // NOTE: /api/hardware-info used to be mapped here TOO — AppConfigEndpoints
    // maps the same route, and two endpoints on one route+method make ASP.NET
    // throw AmbiguousMatchException (HTTP 500) on every request. The endpoint
    // now lives only in AppConfigEndpoints.cs.

    // Nuke everything — useful when a runaway tab piled up stale sessions and
    // the user wants a clean slate without restarting the container. Admin-gated:
    // anonymous callers must not be able to kill every active playback on the box.
    app.MapDelete("/api/hls/sessions", (HlsSessionService hls) =>
    {
        var n = 0;
        foreach (var s in hls.Snapshot()) { hls.Stop(s.Token); n++; }
        return Results.Ok(new { stopped = n });
    })
    .WithName("StopAllHlsSessions")
    .RequireAuthorization(AuthConstants.Policies.SystemSettings);

        return app;
    }
}
