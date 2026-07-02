using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Byte-serving media endpoints: image serving, the in-browser video paths
/// (raw Range file + on-the-fly ffmpeg remux), the external-player .m3u
/// handoff, ffprobe JSON, sideloaded track discovery and subtitle extraction.
///
/// All of these are AllowAnonymous and consumed by cookie-less clients (the
/// browser &lt;video&gt; element, DLNA renderers, the MAUI WebView proxy), so
/// access is gated by <see cref="MediaPathValidator"/> — the path must resolve
/// inside a registered library root — rather than by an auth cookie.
/// </summary>
internal static class MediaStreamEndpoints
{
    public static IEndpointRouteBuilder MapMediaStreamEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── /api/image — serve poster / backdrop images from disk ────────
        // Path must resolve inside a registered library root OR Animarr's
        // dedicated image cache (next to the DB, outside the media tree).
        app.MapGet("/api/image", async (
                string path,
                long? t,
                MediaPathValidator pathValidator,
                HttpContext ctx) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryOrCacheFileAsync(path);
            if (!ok) return earlyResult!;

            var mime = MediaMime.ForImageExtension(Path.GetExtension(fullPath!));
            // Cache-busting: a version stamp (t>0) makes the URL unique per file
            // version → cache immutably for a year; otherwise always revalidate.
            ctx.Response.Headers.CacheControl = t is > 0
                ? "public, max-age=31536000, immutable"
                : "no-cache";
            return Results.File(fullPath!, mime);
        })
        .WithName("GetMediaImage")
        .AllowAnonymous();

    // ─── /api/video — stream video files for in-browser playback ──────────────
    // MP4 / WebM:   served as a static file with HTTP Range support.
    // MKV / AVI /…: remuxed on-the-fly to fragmented MP4 via ffmpeg stream-copy.
    //               No transcode → ~0% CPU, near-line-rate. Browser plays the
    //               fMP4 stream natively through MSE.
    app.MapMethods("/api/video", new[] { "GET", "HEAD" }, async (
            string path,
            MediaPathValidator pathValidator,
            HttpContext http,
            ILoggerFactory loggerFactory) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        var ext = Path.GetExtension(fullPath!).ToLowerInvariant();
        if (MediaMime.IsBrowserNativeContainer(ext))
        {
            var mime = ext switch
            {
                ".mp4"  => "video/mp4",
                ".m4v"  => "video/x-m4v",
                ".mov"  => "video/quicktime",
                ".webm" => "video/webm",
                _       => "video/octet-stream",
            };
            return Results.File(fullPath!, mime, enableRangeProcessing: true);
        }

        // HEAD on the remux endpoint — Vidstack probes the resource before GET to
        // sniff content-type and Range support. Spinning up ffmpeg just to throw
        // the bytes away is wasteful, so reply with headers only. No length (live
        // stream), no Accept-Ranges (browser can't pre-seek the pipe; it can ask
        // for ?seek=N as a query param instead).
        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = "video/mp4";
            http.Response.Headers.CacheControl = "no-store";
            return Results.Empty;
        }

        // Remux path. Stream ffmpeg's stdout to the HTTP response body so the
        // browser starts playing as bytes arrive. No Range support here — MSE
        // buffers what it needs; seeking past buffer triggers a fresh request
        // (the client can pass ?seek=N to ask ffmpeg to start later in the file).
        var seekSec = 0d;
        if (http.Request.Query.TryGetValue("seek", out var seekStr) &&
            double.TryParse(seekStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0)
            seekSec = s;

        var logger = loggerFactory.CreateLogger("ApiVideoRemux");
        var args = new List<string>();
        if (seekSec > 0)
        {
            // -ss BEFORE -i = fast input seek (keyframe-accurate from container index).
            // Cheap on MKV (Matroska has cue points) and works for stream-copy.
            args.Add("-ss"); args.Add(seekSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        // Inspect the video codec once so we can apply codec-specific fixes:
        //  • HEVC needs `-tag:v hvc1` — Chrome/Edge refuse the default `hev1` tag
        //    in fragmented MP4. Same bytes, different fourcc, plays fine.
        //  • DolbyVision-flagged HEVC (profile 8.1 = HDR10-compatible) carries a
        //    DOVI configuration box that browsers can't parse. Strip it via the
        //    hevc_metadata bitstream filter so the player sees plain HDR10.
        var (videoCodec, hasDolbyVision) = await PeekVideoCodecAsync(fullPath!);

        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath! });
        if (seekSec > 0)
        {
            // Shift output PTS so the first frame is at time `seekSec`, not 0.
            // Without this, every seek-reload would reset the timeline to 0:00,
            // confusing both the player UI and our progress-tracking math.
            args.Add("-output_ts_offset");
            args.Add(seekSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        args.AddRange(new[]
        {
            "-map", "0:v:0?",            // first video stream if present
            "-map", "0:a:0?",            // first audio stream only — multi-audio
                                         // selection in fMP4 isn't browser-friendly
            "-c:v", "copy",              // video stream-copy
            // Audio: transcode to AAC. Source can be AC3 / EAC3 / DTS / TrueHD —
            // none of those play reliably in Chrome/Edge. AAC is the lowest common
            // denominator that every browser decodes. 192 kbps stereo is plenty
            // for downmixed surround → headphones / laptop speakers; if the user
            // ever wants 5.1 pass-through we'll add a query toggle.
            "-c:a", "aac",
            "-ac", "2",
            "-b:a", "192k",
        });
        if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            // Critical for Chrome/Edge: refuse the default `hev1` fourcc in fMP4.
            args.Add("-tag:v"); args.Add("hvc1");
        }
        args.AddRange(new[]
        {
            "-f", "mp4",
            // Note on DolbyVision: profile 8.1 files are HDR10-backward-compatible
            // at the HEVC layer, so most browsers can play the stream as-is once
            // the codec tag is hvc1. We do NOT strip the DV RPU here because
            // `hevc_metadata=remove_dovi=1` requires ffmpeg >= 7.1, while the
            // Debian-bundled build is 5.1. If DV-flagged playback breaks again
            // we'll bring our own ffmpeg or transcode.
            // delay_moov: required because AC3 audio packets don't expose their
            // codec frame size until the first frame is muxed; without this flag
            // ffmpeg refuses to emit the (empty) moov and the response is 0 bytes.
            "-movflags", "+frag_keyframe+empty_moov+delay_moov+default_base_moof+separate_moof",
            "pipe:1",
        });

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        System.Diagnostics.Process? proc = null;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffmpeg launch failed for {Path}", fullPath);
            return Results.StatusCode(500);
        }
        if (proc is null) return Results.StatusCode(500);

        // Drain stderr in background so the buffer never blocks. Log warnings —
        // ffmpeg's stream-copy is normally silent so anything here is interesting.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                    logger.LogWarning("ffmpeg: {Line}", line);
            }
            catch { /* process exited */ }
        });

        // When the client disconnects (closes tab / seeks past buffer / etc.)
        // the response stream errors out; kill ffmpeg so we don't leak zombies.
        http.RequestAborted.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        });

        http.Response.ContentType = "video/mp4";
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
        catch (OperationCanceledException) { /* client gone */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "stream pump failed for {Path}", fullPath);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            proc.Dispose();
        }
        return Results.Empty;
    })
    .WithName("GetVideo")
    .AllowAnonymous();
    // ─── /api/file — raw passthrough for DLNA / external native players ──────
    // Streams the source file byte-for-byte with HTTP Range support. No demux,
    // no transcode, no re-mux — TVs, VLC, Kodi, Infuse get bit-identical
    // source content and decode with their own hardware. This is what /api/dlna
    // uses for the cast URL it hands to MediaRenderers, and what DLNA browse
    // returns as item.res URLs. Browsers can use this too for MP4/WebM but
    // typically can't decode MKV containers, hence /api/video stays around as
    // the browser-friendly path that re-muxes.
    app.MapMethods("/api/file", new[] { "GET", "HEAD" }, async (
            string path,
            MediaPathValidator pathValidator,
            HttpContext http) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        var ext = Path.GetExtension(fullPath!).ToLowerInvariant();
        var mime = MediaMime.ForVideoExtension(ext);

        // Cache headers — same file always same content; clients can cache
        // aggressively. The path itself is the cache key because file rename
        // changes the URL.
        http.Response.Headers.CacheControl = "public, max-age=86400";
        http.Response.Headers.AcceptRanges = "bytes";
        // DLNA hint headers — some renderers require these to enable seeking.
        http.Response.Headers["TransferMode.DLNA.ORG"] = "Streaming";
        http.Response.Headers["ContentFeatures.DLNA.ORG"] =
            "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

        return Results.File(fullPath!, mime, enableRangeProcessing: true);
    })
    .WithName("GetRawFile")
    .AllowAnonymous();
    // ─── /api/playlist.m3u — universal external-player handoff ────────────────
    // Returns a tiny .m3u file pointing at /api/file for the requested path.
    // Triggered by the "External Player" icon when the user has picked "m3u"
    // as their default in Settings — every desktop player (VLC, MPC-HC,
    // PotPlayer, mpv standalone, IINA, etc.) opens .m3u files via OS file
    // association, so this is the most portable handoff that requires no
    // per-player URI scheme. Browser downloads the file → user double-clicks
    // or it auto-opens depending on download settings.
    app.MapGet("/api/playlist.m3u", async (
            string path,
            MediaPathValidator pathValidator,
            HttpContext http) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        var fileName     = Path.GetFileNameWithoutExtension(fullPath!);
        var safeFileName = string.Concat(fileName.Where(c => !char.IsControl(c) && c != '"'));
        // Build absolute stream URL — players opening the .m3u won't know our
        // server's hostname otherwise. Use plain HTTP on the raw 8080 port so
        // Caddy's self-signed cert doesn't trip up non-browser TLS stacks.
        var scheme = "http";
        var host   = http.Request.Host.Host;
        var port   = "8080";
        var streamUrl = $"{scheme}://{host}:{port}/api/file?path={Uri.EscapeDataString(fullPath!)}";

        var m3u =
            "#EXTM3U\n" +
            $"#EXTINF:-1,{safeFileName}\n" +
            $"{streamUrl}\n";

        http.Response.Headers.ContentDisposition =
            $"attachment; filename=\"{safeFileName}.m3u\"";
        return Results.Content(m3u, "audio/x-mpegurl");
    })
    .WithName("PlaylistM3u")
    .AllowAnonymous();
    // ─── /api/probe — return ffprobe stream list as JSON for player UI menus ──
    app.MapGet("/api/probe", async (
            string path,
            MediaPathValidator pathValidator,
            ILoggerFactory loggerFactory) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        var logger = loggerFactory.CreateLogger("ApiProbe");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams",
            fullPath!,
        }) psi.ArgumentList.Add(a);

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var json = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync();
                logger.LogWarning("ffprobe non-zero for {Path}: {Err}", fullPath, err);
                return Results.StatusCode(500);
            }
            return Results.Content(json, "application/json");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffprobe failed for {Path}", fullPath);
            return Results.StatusCode(500);
        }
    })
    .WithName("GetProbe")
    .AllowAnonymous();
    // ─── /api/external-tracks — sideload audio/subtitle files for a video ─────
    // Deterministic Tier-0 (sidecar) + Tier-1 (episode bucket) discovery — see
    // ExternalTrackService. Returns ExternalTrackDto[]; the player merges audio
    // entries into the Audio picker (played via a second ffmpeg input) and
    // subtitle entries into the CC picker (converted by /api/subtitle).
    app.MapGet(Animarr.Shared.ApiRoutes.ExternalTracks, async (
            string path,
            MediaPathValidator pathValidator,
            ExternalTrackService externalTracks,
            CancellationToken ct) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;
        var tracks = await externalTracks.FindForVideoAsync(fullPath!, ct);
        return Results.Ok(tracks);
    })
    .WithName("GetExternalTracks")
    .AllowAnonymous();
    // ─── /api/subtitle — extract one subtitle track as VTT (default) or ASS ───
    // Browsers only render WebVTT natively via <track>. For ASS/SSA (anime
    // fansubs) the front-end uses libass-wasm and asks for `?format=ass` here.
    app.MapGet("/api/subtitle", async (
            string path,
            MediaPathValidator pathValidator,
            int track,
            HttpContext http,
            ILoggerFactory loggerFactory) =>
    {
        var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path);
        if (!ok) return earlyResult!;

        var format = (http.Request.Query["format"].ToString() ?? "").ToLowerInvariant();
        var (outFormat, contentType) = format switch
        {
            "ass" or "ssa" => ("ass",  "text/x-ssa; charset=utf-8"),
            _              => ("webvtt", "text/vtt; charset=utf-8"),
        };

        var logger = loggerFactory.CreateLogger("ApiSubtitle");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", fullPath!,
            "-map", $"0:s:{track}",
            "-f", outFormat,
            "pipe:1",
        }) psi.ArgumentList.Add(a);

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            http.Response.ContentType = contentType;
            http.Response.Headers.CacheControl = "public, max-age=3600";
            await p.StandardOutput.BaseStream.CopyToAsync(http.Response.Body, http.RequestAborted);
            await p.WaitForExitAsync();
            return Results.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffmpeg subtitle extract failed for {Path} track {Track}", fullPath, track);
            return Results.StatusCode(500);
        }
    })
    .WithName("GetSubtitle")
    .AllowAnonymous();

        return app;
    }

    /// <summary>Quick ffprobe peek of the first video stream so /api/video can
    /// branch on codec / Dolby-Vision presence. Returns ("hevc", true) for
    /// HDR10-compatible DV profile 8 files.</summary>
    private static async Task<(string? codec, bool hasDolbyVision)> PeekVideoCodecAsync(string fullPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=codec_name:stream_side_data=side_data_type",
                "-print_format", "default=noprint_wrappers=1:nokey=0",
                fullPath,
            }) psi.ArgumentList.Add(a);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (null, false);
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return (null, false);

            string? codec = null;
            bool dv = false;
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("codec_name=", StringComparison.Ordinal))
                    codec = line["codec_name=".Length..].Trim();
                else if (line.StartsWith("side_data_type=", StringComparison.Ordinal)
                      && line.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                    dv = true;
            }
            return (codec, dv);
        }
        catch
        {
            return (null, false);
        }
    }
}
