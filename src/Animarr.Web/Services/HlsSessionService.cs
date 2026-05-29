using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

/// <summary>
/// Manages on-the-fly ffmpeg→HLS sessions for in-browser playback.
///
/// Strategy: **pre-computed VOD playlist + sequential segment encode**, with
/// a per-source choice of container/codec pipeline (<see cref="HlsPlan"/>):
///   • <c>TsStreamCopy</c>       — H.264 source → MPEG-TS HLS, stream-copy.
///     The Jellyfin/Emby/Plex Direct Stream path. PCR timestamps inside TS
///     packets give the player rock-solid A/V sync without any -itsoffset
///     gymnastics; no GPU usage. Audio passes through when the codec is
///     browser-friendly (AAC/AC3/E-AC3/MP3), else re-encoded to AAC.
///   • <c>Fmp4VaapiReencode</c>  — HEVC 8-bit + VAAPI present → fMP4 HLS with
///     h264_vaapi -bf 0 re-encode. Eliminates the B-frame display reorder
///     that caused ~83ms audio-ahead wobble on stream-copy fMP4.
///   • <c>Fmp4StreamCopy</c>     — HEVC 10-bit HDR/DV (Vega 11 can't encode
///     Main10) → fMP4 stream-copy. Accepts residual A/V wobble; recommend
///     DLNA cast for sync-critical viewing of these titles.
///
/// In all three branches:
///   1. <see cref="StartAsync"/> probes the source with ffprobe to learn
///      duration, video codec, audio codec, and resolution.
///   2. We synthesise <c>master.m3u8</c> + <c>media.m3u8</c> ourselves —
///      both contain the full segment list with <c>#EXT-X-ENDLIST</c> and
///      <c>EXT-X-PLAYLIST-TYPE:VOD</c>. The player sees a scrubbable timeline
///      from the very first manifest request.
///   3. ffmpeg runs in the background, writing segments (init.mp4 +
///      seg-NNNNN.m4s for fMP4, seg-NNNNN.ts for MPEG-TS) into the session
///      dir. The segment-serving endpoint waits up to
///      <see cref="SegmentWaitTimeout"/> for the requested file to appear.
///
/// Why this matters: with ffmpeg's EVENT-style playlist, hls.js treated the
/// stream as live broadcast, hiding the scrub bar and pinning to the live
/// edge. With our pre-written VOD playlist + ENDLIST the player allows
/// arbitrary seeking; segments past ffmpeg's current encoding point wait,
/// stream-copy being disk-IO-bound at ~10× realtime even for 4K HEVC.
///
/// Why MPEG-TS for H.264: Plex/Jellyfin/Emby all use TS as the workhorse
/// because (1) PCR-based sync sidesteps fMP4's TFDT-PTS interaction
/// headaches, (2) browsers + TVs have decoded TS for two decades, (3) audio
/// passthrough Just Works inside TS without container-level mangling.
/// </summary>
public sealed class HlsSessionService : IDisposable
{
    /// <summary>How long a session can have no keepalive ping before the GC
    /// reaps it. The JS bridge pings every 30s while the player is mounted,
    /// so 5 min == 10 missed pings — well past "transient network blip".</summary>
    public static readonly TimeSpan IdleTimeout       = TimeSpan.FromMinutes(5);
    // 60 sec matches the hls.js fragLoadingTimeOut we set client-side
    // (animarr-player.js). When the client times out first, the server's
    // long wait is wasted — by then ffmpeg may have finally produced the
    // segment but no one is listening. Keep both in sync so a real
    // server-side give-up only happens when ffmpeg genuinely can't deliver.
    public static readonly TimeSpan SegmentWaitTimeout = TimeSpan.FromSeconds(60);
    private const double SegmentDurationSec = 6.0;
    /// <summary>Hard cap on concurrent active sessions across the whole host.
    /// Single-user Animarr realistically has 1-2 (one main player + maybe a
    /// background tab); anything past this is almost certainly leaked sessions
    /// from tab-close-without-detach, and each one ties up an ffmpeg + tmpfs
    /// dir, so we evict the oldest LRU when we hit the cap.</summary>
    private const int MaxConcurrentSessions = 5;

    private readonly ILogger<HlsSessionService> _logger;
    private readonly HardwareInfoService? _hardware;
    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly Timer _gcTimer;

    public HlsSessionService(ILogger<HlsSessionService> logger, HardwareInfoService? hardware = null)
    {
        _logger  = logger;
        _hardware = hardware;
        // /tmp on Linux is tmpfs by default on most distros — segments live
        // in RAM and never hit the SSD. Fall back to OS temp on Windows dev.
        var root = OperatingSystem.IsLinux() ? "/tmp/animarr-hls" : Path.Combine(Path.GetTempPath(), "animarr-hls");
        Directory.CreateDirectory(root);
        _rootDir = root;

        // Best-effort cleanup of leftover dirs from a previous process.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_rootDir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "HLS startup cleanup failed"); }

        // GC every 30s — frequent enough that a crashed ffmpeg is reaped
        // before the player gives up retrying segments (hls.js typically
        // backs off after a few 503s).
        _gcTimer = new Timer(_ => GarbageCollect(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returned from <see cref="StartAsync"/>. <c>TotalDurationSec</c> is the
    /// full file's duration (not the playlist's, which is shortened by the
    /// resume seek). The client needs this to report progress against the
    /// real movie length, otherwise resuming would silently truncate the
    /// stored watch position on every replay.
    /// </summary>
    public sealed record StartResult(string Token, string ManifestRelativeUrl, double TotalDurationSec,
        PlayerOutputInfo Output);

    /// <summary>
    /// Returned from <see cref="ChoosePlaybackAsync"/>. When <c>DirectPlay</c>
    /// is true, the client should set the player's src to <c>DirectUrl</c>
    /// directly and skip the HLS session entirely. Otherwise the caller falls
    /// through to <see cref="StartAsync"/> for the HLS path.
    /// </summary>
    public sealed record PlaybackDecision(bool DirectPlay, string? DirectUrl, double DurationSec,
        PlayerOutputInfo? Output);

    /// <summary>
    /// Describes what the player actually receives — NOT what's on disk. The
    /// distinction matters for transcoded paths: a HEVC 10-bit DV source goes
    /// through VAAPI re-encode and the player sees H.264 8-bit SDR; a HEVC
    /// 10-bit stream-copy passes HDR through unchanged. The HUD's right-side
    /// meta plashka reads from this so the user sees "what's actually playing
    /// right now", not "what the source file claims to be".
    ///
    /// Plan values mirror <c>HlsPlan</c> (kept as strings to avoid leaking the
    /// internal enum through the API surface):
    ///   • "directplay"      — source plays as-is via /api/file
    ///   • "ts-copy"         — H.264 source remuxed to MPEG-TS for HLS
    ///   • "vaapi-reencode"  — HEVC 8-bit → h264 via VAAPI (HDR lost)
    ///   • "nvenc-reencode"  — HEVC any-bit → h264 via NVENC (HDR lost)
    ///   • "fmp4-copy"       — HEVC 10-bit/HDR/DV passed through unchanged
    /// </summary>
    public sealed record PlayerOutputInfo(
        string  Plan,
        string  Container,
        string  VideoCodec,
        int     BitDepth,
        string  Hdr,
        string[] HdrFormats,
        int     Width,
        int     Height,
        string  AudioCodec,
        int     AudioChannels,
        string  AudioLanguage,
        bool    Transcoded,
        string? TranscodeReason);

    /// <summary>
    /// Probe the source file and decide whether the browser can play it
    /// natively (Direct Play) or whether we need to spin up an HLS session.
    ///
    /// Direct Play criteria: MP4/M4V container + H.264 video (8-bit, no DV)
    /// + AAC/MP3 audio. These play in every contemporary browser without any
    /// re-mux/transcode, with perfect A/V sync and instant start. Everything
    /// else (MKV containers, HEVC video, AC3/DTS/TrueHD audio) goes through
    /// our HLS pipeline where the appropriate <see cref="HlsPlan"/> is picked.
    /// </summary>
    public async Task<PlaybackDecision> ChoosePlaybackAsync(string fullPath, CancellationToken ct = default)
    {
        var probe = await ProbeMediaAsync(fullPath, ct);
        var duration = probe?.DurationSec ?? 0;
        if (probe is null) return new PlaybackDecision(false, null, duration, null);

        var container = Path.GetExtension(fullPath).ToLowerInvariant().TrimStart('.');
        if (!IsDirectPlayEligible(container, probe))
            return new PlaybackDecision(false, null, duration, null);

        // /api/file serves the raw bytes with Range support — exactly what
        // <video> needs for native seek. URL-escape the path so spaces and
        // unicode survive (file paths frequently have both).
        var directUrl = "/api/file?path=" + Uri.EscapeDataString(fullPath);
        var output = BuildOutputInfo(probe, plan: null, isDirectPlay: true);
        return new PlaybackDecision(true, directUrl, duration, output);
    }

    /// <summary>
    /// Builds the <see cref="PlayerOutputInfo"/> the HUD reads off — the
    /// stream the player actually receives after our serving decision. For
    /// direct play and stream-copy paths it mirrors the source; for re-encode
    /// paths it reports the post-transcode codec/bit-depth/HDR state.
    /// </summary>
    private static PlayerOutputInfo BuildOutputInfo(ProbeInfo probe, HlsPlan? plan, bool isDirectPlay)
    {
        // Re-encode paths flatten to H.264 8-bit SDR. Everything else preserves
        // the source bitstream (Direct Play, TS copy, fMP4 stream-copy).
        bool isReencode = plan is HlsPlan.Fmp4VaapiReencode
                               or HlsPlan.Fmp4NvencReencode
                               or HlsPlan.Fmp4SoftwareReencode;

        var hdrFormats = new List<string>();
        if (!isReencode)
        {
            if (probe.HasDolbyVision) hdrFormats.Add("dolbyvision");
            var xfer = (probe.ColorTransfer ?? "").ToLowerInvariant();
            if (xfer == "smpte2084") hdrFormats.Add("hdr10");
            else if (xfer == "arib-std-b67") hdrFormats.Add("hlg");
        }
        var hdr = hdrFormats.Count > 0 ? hdrFormats[0] : "sdr";

        string videoCodec = isReencode ? "h264" : (probe.VideoCodec ?? "");
        int    bitDepth   = isReencode ? 8       : (probe.Is10Bit ? 10 : 8);

        string container = isDirectPlay      ? "mp4"
                         : plan == HlsPlan.TsStreamCopy ? "mpegts"
                         :                                "fmp4";

        string planName = isDirectPlay ? "directplay"
            : plan switch
            {
                HlsPlan.TsStreamCopy         => "ts-copy",
                HlsPlan.Fmp4VaapiReencode    => "vaapi-reencode",
                HlsPlan.Fmp4NvencReencode    => "nvenc-reencode",
                HlsPlan.Fmp4SoftwareReencode => "sw-reencode",
                HlsPlan.Fmp4StreamCopy       => "fmp4-copy",
                _                            => "unknown",
            };

        string? reason = isDirectPlay ? null : plan switch
        {
            HlsPlan.TsStreamCopy      => "H.264 source remuxed to MPEG-TS for HLS delivery",
            HlsPlan.Fmp4VaapiReencode => "HEVC 8-bit re-encoded to H.264 via VAAPI for browser compatibility (HDR lost)",
            HlsPlan.Fmp4NvencReencode => "HEVC re-encoded to H.264 via NVENC for browser compatibility (HDR lost)",
            HlsPlan.Fmp4SoftwareReencode => "Re-encoded to H.264 via CPU (libx264) for the requested quality (HDR lost)",
            HlsPlan.Fmp4StreamCopy    => "HEVC 10-bit / HDR / DV stream-copied to fMP4 (HDR preserved if browser can decode)",
            _                          => null,
        };

        return new PlayerOutputInfo(
            Plan:            planName,
            Container:       container,
            VideoCodec:      videoCodec,
            BitDepth:        bitDepth,
            Hdr:             hdr,
            HdrFormats:      hdrFormats.ToArray(),
            Width:           probe.Width,
            Height:          probe.Height,
            AudioCodec:      probe.AudioCodec ?? "",
            AudioChannels:   probe.AudioChannels,
            AudioLanguage:   probe.AudioLanguage ?? "",
            Transcoded:      !isDirectPlay,
            TranscodeReason: reason);
    }

    private static bool IsDirectPlayEligible(string container, ProbeInfo probe)
    {
        // Container must be browser-native MP4. MKV plays in Chrome but not
        // Safari/Firefox; .mov works in Safari only. Sticking to .mp4/.m4v
        // gives us the widest cross-browser coverage with zero risk.
        if (container != "mp4" && container != "m4v") return false;

        // H.264 8-bit only. HEVC in MP4 plays on Safari/iOS but not on
        // Chrome/Firefox; 10-bit isn't universally supported even where the
        // codec is; DV layer needs a DV-aware decoder.
        if (!string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase)) return false;
        if (probe.Is10Bit) return false;
        if (probe.HasDolbyVision) return false;

        // Audio: AAC and MP3 are the only universally-supported codecs inside
        // MP4. AC3/E-AC3 in MP4 is browser-spotty; DTS/TrueHD not at all.
        var ac = (probe.AudioCodec ?? "").ToLowerInvariant();
        if (ac != "aac" && ac != "mp3" && ac != "mp4a") return false;

        return true;
    }

    public async Task<StartResult> StartAsync(string fullPath, double seekSec, CancellationToken ct = default,
        double audioOffsetHwSec = 0.07, double audioOffsetSwSec = 0.0,
        // Index of the audio stream inside the source to use (`-map 0:a:N?`).
        // 0 = first audio (the historical behaviour). The HUD's Audio popup
        // restarts the session with a different index when the user picks
        // another track — there's no way to switch tracks live because our
        // HLS pipeline transcodes a single audio stream.
        int audioTrackIndex = 0,
        // Cap output to this height (e.g. 1080 / 720). 0 = native resolution.
        // When below the source height it forces a re-encode (you can't shrink
        // a stream-copy). The HUD's Quality popup restarts the session with a
        // new cap, same as audio-track switching.
        int maxHeight = 0,
        // Absolute path to an EXTERNAL audio file (a sideload dub track that
        // isn't muxed in the source). When set, ffmpeg takes it as a second
        // input and maps `1:a:{audioTrackIndex}` instead of the source's audio
        // — see BuildFfmpegArgs. The video plan (TS vs fMP4) is unaffected; only
        // the audio mapping changes. null = use the source's own audio.
        string? externalAudioPath = null)
    {
        // ─── Pre-flight cleanup: dedupe + cap ──────────────────────────────
        // 1) Kill EXISTING sessions for the same source file — a second Play
        //    on the same movie means the user wants the new one. We exempt
        //    very recently created sessions (<3s) because they're still in
        //    their startup wait loop, and tearing them down mid-startup is
        //    what caused "first attempt doesn't start, retry works" — the
        //    very-fresh session got its dir nuked while still racing to emit
        //    init.mp4, then this new request kicks off the same encoding
        //    again from scratch.
        var dedupCutoff = DateTime.UtcNow - TimeSpan.FromSeconds(3);
        var stalePathSessions = _sessions
            .Where(kv => string.Equals(kv.Value.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase)
                      && kv.Value.CreatedAt < dedupCutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var t in stalePathSessions)
        {
            _logger.LogInformation("HLS: reaping prior session {Token} for same source", t);
            Stop(t);
        }

        // 2) Enforce the global cap. If we're at the limit AFTER the dedup
        //    step, evict the oldest-activity session — this catches the case
        //    where the user has multiple tabs open on different files and
        //    has accumulated leaked sessions from earlier closes.
        while (_sessions.Count >= MaxConcurrentSessions)
        {
            var oldest = _sessions
                .OrderBy(kv => kv.Value.LastActive)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (oldest is null) break;
            _logger.LogInformation("HLS: at session cap, evicting LRU {Token}", oldest);
            Stop(oldest);
        }

        var token = Guid.NewGuid().ToString("N");
        var dir   = Path.Combine(_rootDir, token);
        Directory.CreateDirectory(dir);

        // 1. Probe so we know the duration + codec hints upfront. Without
        //    duration we can't build a VOD playlist — fall back to live mode.
        var probe = await ProbeMediaAsync(fullPath, ct);
        if (probe is null || probe.DurationSec <= 0)
        {
            _logger.LogWarning("HLS: probe failed for {Path}, falling back to live encoding mode", fullPath);
            // Even if probe fails, try ffmpeg anyway — the live-style playlist
            // is degraded UX but better than refusing playback.
        }

        // Playlist always spans the FULL file duration — the player UI should
        // show the actual movie length on the scrub bar, not "remaining time
        // after the resume seek". We use HLS's #EXT-X-START tag below to
        // tell the player to begin playback at `seekSec` while still seeing
        // the full timeline.
        var totalDuration = probe?.DurationSec ?? 0;
        // Pick the playback strategy from the probe. Three branches, see HlsPlan:
        //   • TsStreamCopy        — H.264 source, MPEG-TS HLS, no GPU, PCR sync
        //   • Fmp4VaapiReencode   — HEVC 8-bit, fMP4 + h264_vaapi, -bf 0 sync
        //   • Fmp4StreamCopy      — HEVC 10-bit HDR/DV, fMP4 stream-copy
        // The plan affects ffmpeg argv, segment file extension, and what CODECS
        // attribute we advertise in master.m3u8.
        // Effective downscale target: only when the requested cap is BELOW the
        // source height. 0 = native resolution (no scaling).
        var targetHeight = (maxHeight > 0 && (probe?.Height ?? 0) > 0 && maxHeight < probe!.Height)
            ? maxHeight : 0;
        var plan = probe is not null ? ChoosePlan(probe, maxHeight) : HlsPlan.Fmp4StreamCopy;
        ProbeInfo? probeForPlaylist = probe;
        if (probe is not null && plan is HlsPlan.Fmp4VaapiReencode
                                      or HlsPlan.Fmp4NvencReencode
                                      or HlsPlan.Fmp4SoftwareReencode)
        {
            // All three re-encode to H.264 8-bit → master playlist must advertise
            // avc1, not the source's hvc1 (hls.js gates MSE capability on this).
            // Reflect the downscaled resolution too so the RESOLUTION hint is sane.
            int outH = targetHeight > 0 ? targetHeight : probe.Height;
            int outW = targetHeight > 0 && probe.Height > 0
                ? (int)Math.Round((double)probe.Width * targetHeight / probe.Height / 2) * 2
                : probe.Width;
            probeForPlaylist = probe with
            {
                CodecsAttribute = "avc1.4d401f,mp4a.40.2",
                Width  = outW,
                Height = outH,
            };
        }
        else if (probe is not null && plan == HlsPlan.TsStreamCopy)
        {
            // MPEG-TS branch: declare the source-native CODECS so browsers know
            // what they're getting. Audio codec depends on passthrough decision.
            var audioCodec = IsBrowserCompatibleAudioInTs(probe.AudioCodec)
                ? probe.AudioCodec switch
                {
                    "ac3"   => "ac-3",
                    "eac3"  => "ec-3",
                    "mp3"   => "mp4a.40.34",
                    _       => "mp4a.40.2",  // AAC
                }
                : "mp4a.40.2";
            probeForPlaylist = probe with { CodecsAttribute = $"avc1.640028,{audioCodec}" };
        }
        var segCount = totalDuration > 0
            ? (int)Math.Ceiling(totalDuration / SegmentDurationSec)
            : 0;
        // ffmpeg starts encoding at the resume point, so the first segment
        // it produces is seg-K where K = floor(seekSec / segDur). Earlier
        // segments don't exist on disk; the player learns to skip past them
        // via #EXT-X-START + the WaitForFileAsync seek-restart logic on
        // backward scrubs.
        var startSegment = seekSec > 0
            ? (int)Math.Floor(seekSec / SegmentDurationSec)
            : 0;

        // 2. Write synthetic master + media playlists. If we couldn't probe
        //    duration, skip this and let ffmpeg's HLS muxer write them
        //    EVENT-style (the player will fall back to live UX).
        bool prewrittenPlaylists = false;
        if (segCount > 0 && probe is not null)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "master.m3u8"),
                    BuildMasterPlaylist(probeForPlaylist!), Encoding.ASCII);
                File.WriteAllText(Path.Combine(dir, "media.m3u8"),
                    BuildMediaPlaylist(totalDuration, segCount, plan, startOffsetSec: seekSec), Encoding.ASCII);
                prewrittenPlaylists = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HLS: failed to pre-write playlists for {Token}", token);
            }
        }

        // 3. ffmpeg writes (init.mp4 + segments for fMP4) or (just segments
        //    for MPEG-TS). If we pre-wrote our playlists, ffmpeg writes to a
        //    scratch file we ignore (so it doesn't trample our VOD playlists
        //    with its own EVENT-style updates).
        var scratchPlaylist = Path.Combine(dir, "_ffmpeg.m3u8");
        var ffmpegMediaPath = prewrittenPlaylists ? scratchPlaylist : Path.Combine(dir, "media.m3u8");
        var ffmpegMasterName = prewrittenPlaylists ? "_ffmpeg_master.m3u8" : "master.m3u8";

        // Pick the per-channel audio offset matching the plan we just chose:
        //   HW VAAPI re-encode → uses the "hw" calibration value (auto-calib
        //   capable, defaults to 70ms based on browser chain latency).
        //   fMP4 stream-copy → uses the "sw" slider value (manual only — the
        //   calibration test clip isn't representative of stream-copy timing).
        //   TS stream-copy → uses 0; PCR inside TS handles A/V sync natively
        //   and any -itsoffset would just throw it off.
        var audioOffsetSec = externalAudioPath is not null
            // External dub sync is a different problem from the source's own A/V
            // wobble (it depends on how the dub was authored vs the video cut),
            // so we route it through the manual SW slider for EVERY video plan —
            // including TS, which otherwise applies no offset. The player forces
            // the Sync control to the 'sw' channel whenever an external track is
            // active so the user has a knob to nudge it.
            ? audioOffsetSwSec
            : plan switch
            {
                HlsPlan.Fmp4VaapiReencode    => audioOffsetHwSec,
                HlsPlan.Fmp4NvencReencode    => audioOffsetHwSec,  // same -bf 0 sync profile as VAAPI
                HlsPlan.Fmp4SoftwareReencode => audioOffsetHwSec,  // libx264 -bf 0, same profile
                HlsPlan.Fmp4StreamCopy       => audioOffsetSwSec,
                HlsPlan.TsStreamCopy         => 0.0,
                _                            => 0.0,
            };
        // start_number tells ffmpeg's HLS muxer to name its first segment
        // seg-{startSegment}.{ext} so the on-disk layout matches the playlist
        // we wrote. PTS handling depends on the plan (see BuildFfmpegArgs).
        _logger.LogInformation("HLS {Token}: plan={Plan} offset={OffsetMs}ms (codec={Vcodec}/{Acodec} {W}x{H})",
            token, plan, (int)Math.Round(audioOffsetSec * 1000),
            probe?.VideoCodec, probe?.AudioCodec, probe?.Width, probe?.Height);
        var args = BuildFfmpegArgs(fullPath, seekSec, dir, ffmpegMediaPath, ffmpegMasterName,
            probe, plan, startNumber: startSegment, reuseInit: false,
            targetHeight: targetHeight,
            audioOffsetSec: audioOffsetSec, audioTrackIndex: audioTrackIndex,
            externalAudioPath: externalAudioPath);
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            // Don't redirect stdout — ffmpeg's stderr carries all the logs.
            // Redirecting stdout without draining it fills the 64KB pipe
            // buffer and BLOCKS ffmpeg mid-segment-write once any process
            // pollutes stdout (some libavformat versions print a banner
            // there even with -hide_banner). Just let stdout go to the
            // container's /dev/null so ffmpeg never blocks.
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ffmpeg launch failed for HLS session {Token} ({Path})", token, fullPath);
            TryRemoveDir(dir);
            throw;
        }

        var session = new HlsSession(token, fullPath, dir, proc, seekSec, segCount,
            probe?.VideoCodec, plan, audioOffsetSec, totalDuration, audioTrackIndex, targetHeight,
            externalAudioPath);
        _sessions[token] = session;

        // Drain stderr — visible at Warning level so genuine encoder issues
        // (codec param mismatch, broken inputs) surface in `docker logs`.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                    _logger.LogWarning("[hls {Token}] {Line}", token, line);
            }
            catch { /* process exited */ }
            try { await proc.WaitForExitAsync(); } catch { }
            _logger.LogInformation("HLS session {Token} ffmpeg exited (code {Code})", token, proc.HasExited ? proc.ExitCode : -1);
        });

        // 4. Wait for ffmpeg's initial output before returning the manifest URL.
        //    Two-phase wait:
        //
        //    (A) HARD wait for FIRST segment + init + master.m3u8 to exist
        //        (60s ceiling). Without these the manifest URL is useless;
        //        the player would get 404s/503s on the first fetch.
        //
        //    (B) SOFT wait for two MORE segments to be produced beyond the
        //        first (5s grace period). Why: on a cold OS page cache
        //        (typically the first playback after a page reload of a
        //        huge UHD MKV), ffmpeg takes 10-30s just to seek + parse
        //        the container. By the time it flushes seg-K, the wall
        //        clock is well past where the player would catch up. If
        //        we returned right at seg-K, the player consumes its
        //        ~5s of buffer faster than ffmpeg can produce seg-K+1 →
        //        bufferStalledError → frozen. Holding 3-4s for two more
        //        segments lets ffmpeg get ahead, giving the player a
        //        ~18s cushion. On warm cache (second+ playback) this
        //        grace period elapses with 2+ segments ready in 1-2s,
        //        so there's no visible startup delay.
        //
        //    (C) Same exit conditions both phases: if ffmpeg dies, throw.
        var hardDeadline = DateTime.UtcNow.AddSeconds(60);
        var segExt = SegmentExtension(plan);
        var initPath = Path.Combine(dir, "init.mp4");
        var firstSegPath = Path.Combine(dir, $"seg-{startSegment:D5}.{segExt}");
        var masterPath = Path.Combine(dir, "master.m3u8");
        bool needsInit = plan != HlsPlan.TsStreamCopy;
        // Phase A — wait for first segment.
        while (DateTime.UtcNow < hardDeadline)
        {
            var ready = (!needsInit || File.Exists(initPath))
                     && File.Exists(firstSegPath)
                     && File.Exists(masterPath);
            if (ready) break;
            if (proc.HasExited)
            {
                _sessions.TryRemove(token, out _);
                TryRemoveDir(dir);
                throw new InvalidOperationException($"ffmpeg exited before producing init/first segment (code {proc.ExitCode}).");
            }
            await Task.Delay(150, ct);
        }
        // Phase B — grace period for two more segments so player has buffer.
        var thirdSegPath = Path.Combine(dir, $"seg-{(startSegment + 2):D5}.{segExt}");
        var graceDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < graceDeadline)
        {
            if (File.Exists(thirdSegPath)) break;
            if (proc.HasExited) break;  // ffmpeg done early — fine, return with what we have
            await Task.Delay(100, ct);
        }
        _logger.LogInformation("HLS {Token}: startup ready, returning manifest (warmedSegments={Count})",
            token, Directory.EnumerateFiles(dir, $"seg-*.{segExt}").Count());

        // Output info describes what the player actually receives — the
        // post-transcode codec/HDR/container for the HUD's right-side meta
        // plashka. Built from probe + plan so re-encode paths correctly
        // report "h264 8-bit SDR" rather than echoing the source's HEVC HDR.
        // Falls back to a defaults block if probe was null (live mode).
        // Use probeForPlaylist so a downscaled re-encode reports the OUTPUT
        // resolution (it carries the scaled Width/Height), not the source.
        var output = probe is not null
            ? BuildOutputInfo(probeForPlaylist ?? probe, plan, isDirectPlay: false)
            : new PlayerOutputInfo(
                Plan:            "unknown",
                Container:       plan == HlsPlan.TsStreamCopy ? "mpegts" : "fmp4",
                VideoCodec:      "",
                BitDepth:        8,
                Hdr:             "sdr",
                HdrFormats:      Array.Empty<string>(),
                Width:           0,
                Height:          0,
                AudioCodec:      "",
                AudioChannels:   0,
                AudioLanguage:   "",
                Transcoded:      true,
                TranscodeReason: "Probe failed — output info unavailable");
        return new StartResult(token, $"/api/hls/{token}/master.m3u8", probe?.DurationSec ?? 0, output);
    }

    public string? GetSessionDir(string token)
    {
        if (!_sessions.TryGetValue(token, out var s)) return null;
        s.Touch();
        return s.OutputDir;
    }

    public bool Touch(string token)
    {
        if (!_sessions.TryGetValue(token, out var s)) return false;
        s.Touch();
        return true;
    }

    /// <summary>
    /// Wait for a specific segment file to appear. If the requested segment
    /// is far ahead of ffmpeg's current encoding position, kill the running
    /// ffmpeg and restart it at the target segment — without this, scrubbing
    /// to 1:30:00 of a movie forces the user to wait for ffmpeg to walk
    /// through the first 1h30m of content sequentially, which can take many
    /// tens of seconds even at stream-copy speeds. Used by the segment-
    /// serving endpoint.
    /// </summary>
    public async Task<string?> WaitForFileAsync(string token, string fileName, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        var full = Path.Combine(session.OutputDir, fileName);
        if (File.Exists(full)) return full;

        // If the request is for a segment (seg-NNNNN.{m4s|ts}) and it's not
        // on disk yet, decide whether to wait or to skip ffmpeg ahead.
        var targetSeg = ParseSegmentNumber(fileName);
        if (targetSeg >= 0)
        {
            var producedTop = HighestProducedSegment(session.OutputDir);
            var gap = targetSeg - producedTop;
            // Three cases:
            //   • forward, small gap (1..threshold): ffmpeg will reach it
            //     shortly, cheaper to wait than to restart.
            //   • forward, big gap (>threshold): restart at target so we
            //     don't make the user wait minutes for sequential encode.
            //   • backward/at-or-below (target <= producedTop) and the file
            //     still isn't there: either the user scrubbed before the
            //     resume point's start_number, or a segment got skipped/
            //     deleted. Restart at target to produce it.
            const int restartThreshold = 12;
            bool needsRestart = gap > restartThreshold
                            || (targetSeg <= producedTop && producedTop >= 0);
            // Edge case: producedTop = -1 (no segments produced yet) and
            // target = startSegment. ffmpeg just hasn't flushed its first
            // segment yet — wait, don't thrash.
            if (producedTop < 0 && Math.Abs(gap) > restartThreshold)
                needsRestart = true;
            if (needsRestart)
            {
                _logger.LogInformation("HLS {Token}: seek-jump from seg-{From} to seg-{To}, restarting ffmpeg",
                    token, producedTop, targetSeg);
                await RestartFfmpegAtSegmentAsync(session, targetSeg, ct);
            }
        }

        var deadline = DateTime.UtcNow + SegmentWaitTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (File.Exists(full)) return full;
            if (session.Process.HasExited && session.Process.ExitCode != 0) return null;
            if (session.Process.HasExited) return File.Exists(full) ? full : null;
            await Task.Delay(150, ct);
        }
        return null;
    }

    private static int ParseSegmentNumber(string fileName)
    {
        // seg-NNNNN.{m4s|ts} → NNNNN. Returns -1 for non-segment files
        // (init.mp4, *.m3u8) so the caller skips the seek-restart path.
        if (!fileName.StartsWith("seg-", StringComparison.Ordinal)) return -1;
        var dot = fileName.IndexOf('.');
        if (dot <= 4) return -1;
        // Only accept the segment extensions we actually produce.
        var ext = fileName[(dot + 1)..].ToLowerInvariant();
        if (ext != "m4s" && ext != "ts") return -1;
        var numStr = fileName[4..dot];
        return int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    private static int HighestProducedSegment(string dir)
    {
        // Scan the session dir for the highest seg-NNNNN.{m4s|ts} file ffmpeg
        // has finished writing (we use +temp_file so partials are *.tmp).
        int max = -1;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "seg-*.*"))
            {
                var name = Path.GetFileName(path);
                var n = ParseSegmentNumber(name);
                if (n > max) max = n;
            }
        }
        catch { /* ignore — dir might be torn down */ }
        return max;
    }

    /// <summary>Segment file extension for a given plan. MPEG-TS uses .ts,
    /// the fMP4 variants use .m4s.</summary>
    private static string SegmentExtension(HlsPlan plan) =>
        plan == HlsPlan.TsStreamCopy ? "ts" : "m4s";

    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private async Task RestartFfmpegAtSegmentAsync(HlsSession session, int targetSegment, CancellationToken ct)
    {
        // Serialise restarts so a flurry of player seeks (the seek bar fires
        // multiple `seeking` events as the user drags) can't spawn racing
        // ffmpegs that overwrite each other's output.
        await _restartLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock — if another scrub
            // produced this exact segment while we were waiting, no need
            // to restart. We compare on the target FILE rather than
            // "highest produced >= target" because ffmpeg may have already
            // restarted at a different (higher) offset, leaving our target
            // still un-produced.
            var segExt = SegmentExtension(session.Plan);
            var targetFile = Path.Combine(session.OutputDir, $"seg-{targetSegment:D5}.{segExt}");
            if (File.Exists(targetFile)) return;

            // Kill running ffmpeg (if any) and wait for clean exit.
            try { if (!session.Process.HasExited) session.Process.Kill(true); } catch { }
            try
            {
                using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                killCts.CancelAfter(2000);
                await session.Process.WaitForExitAsync(killCts.Token);
            }
            catch { /* either timed out or already gone */ }

            // Sweep stale .tmp files from the killed ffmpeg's partial writes —
            // otherwise the new ffmpeg's atomic rename can race against them.
            try
            {
                foreach (var t in Directory.EnumerateFiles(session.OutputDir, "*.tmp"))
                    File.Delete(t);
            }
            catch { }

            // The playlist now spans the full file duration, so playlist time
            // for seg-K is exactly K*segDur — meaning ffmpeg should seek to
            // K*segDur of the SOURCE, NOT to session.SeekSec + K*segDur. The
            // old math (adding the initial resume offset) was a leftover from
            // when the playlist was shortened by the resume seek; with the
            // full-duration playlist that addition would double-count the
            // offset and seek 22 min past the user's actual scrub target.
            var sourceSeekSec = targetSegment * SegmentDurationSec;
            // We don't have the original probe on restart — but the plan is
            // fixed for the session's lifetime and BuildFfmpegArgs only needs
            // the video codec hint (for the `-tag:v hvc1` on stream-copy HEVC).
            // Pass a minimal ProbeInfo carrying just that.
            var miniProbe = new ProbeInfo(0, session.VideoCodec, null, 0, 0, null, false, false, null, 0, null);
            var args = BuildFfmpegArgs(session.SourcePath, sourceSeekSec, session.OutputDir,
                Path.Combine(session.OutputDir, "_ffmpeg.m3u8"),
                "_ffmpeg_master.m3u8",
                miniProbe,
                session.Plan,
                startNumber: targetSegment,
                reuseInit: true,
                targetHeight: session.MaxHeight,
                audioOffsetSec: session.AudioOffsetSec,
                audioTrackIndex: session.AudioTrackIndex,
                externalAudioPath: session.ExternalAudioPath);

            var psi = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process? proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HLS: ffmpeg restart failed for {Token}", session.Token);
                return;
            }
            if (proc is null) return;

            session.SwapProcess(proc);

            // Tell the playlist that segments from `targetSegment` onward come
            // from a fresh encoder context — without this marker hls.js tries
            // to continue the previous decoder pipeline through the gap and
            // produces the ~300ms audio-ahead glitch after every scrub.
            session.AddRestartBoundary(targetSegment);
            RegenerateMediaPlaylist(session, session.TotalDurationSec);

            // Drain stderr so the pipe doesn't fill + block ffmpeg.
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                        _logger.LogWarning("[hls {Token} +seg{Seg}] {Line}", session.Token, targetSegment, line);
                }
                catch { }
                try { await proc.WaitForExitAsync(); } catch { }
            });
        }
        finally
        {
            _restartLock.Release();
        }
    }

    public void Stop(string token)
    {
        if (!_sessions.TryRemove(token, out var s)) return;
        try { if (!s.Process.HasExited) s.Process.Kill(true); } catch { }
        try { s.Process.Dispose(); } catch { }
        TryRemoveDir(s.OutputDir);
        _logger.LogInformation("HLS session {Token} stopped (by client / GC).", token);
    }

    // ─── Synthetic playlist generation ─────────────────────────────────────

    private sealed record ProbeInfo(double DurationSec, string? VideoCodec, string? AudioCodec,
        int Width, int Height, string? CodecsAttribute, bool HasDolbyVision, bool Is10Bit,
        // 2026-05-27: extended for PlayerOutputInfo. ColorTransfer drives
        // HDR10 / HLG detection (smpte2084 vs arib-std-b67), AudioChannels +
        // AudioLanguage feed the right-side meta plashka in the player HUD.
        string? ColorTransfer, int AudioChannels, string? AudioLanguage);

    /// <summary>
    /// What ffmpeg invocation + segment shape this session needs. Decided once
    /// from the probe and stuck to for the session's lifetime.
    /// </summary>
    private enum HlsPlan
    {
        /// <summary>H.264 video → MPEG-TS HLS with stream-copy video + audio.
        /// PCR-based A/V sync inside TS solves the "audio ahead" wobble that
        /// our fMP4 path has. No GPU needed.</summary>
        TsStreamCopy,

        /// <summary>HEVC 8-bit → fMP4 HLS with VAAPI re-encode to H.264 +bf 0.
        /// AMD GCN+/Intel Gen9+ can encode 8-bit; re-encode flattens B-frame
        /// reorder.</summary>
        Fmp4VaapiReencode,

        /// <summary>HEVC 8/10-bit → fMP4 HLS with NVENC re-encode to H.264.
        /// NVIDIA NVENC can handle BOTH 8-bit and 10-bit HEVC decode + H.264
        /// encode (Pascal+), so this plan beats Fmp4StreamCopy for any HEVC
        /// content when an NVIDIA GPU is available.</summary>
        Fmp4NvencReencode,

        /// <summary>HEVC 10-bit HDR / DV → fMP4 HLS with stream-copy video.
        /// Used when neither VAAPI (Vega 11 can't encode Main10) nor NVENC
        /// is available. Some residual A/V wobble; recommend DLNA cast or
        /// mpv for sync-critical HDR/DV playback.</summary>
        Fmp4StreamCopy,

        /// <summary>Software (libx264) re-encode to H.264 + optional downscale.
        /// The universal CPU fallback for the quality-ladder downscale path when
        /// no usable GPU encoder is present (no NVENC, no VAAPI). CPU-heavy —
        /// especially decoding 4K HEVC 10-bit in software — but it's the only
        /// way to honour a "give me 1080p" request on a GPU-less host. Per the
        /// project rule: GPU if present, else CPU, and if the box can't keep up
        /// that's on the operator (don't downscale).</summary>
        Fmp4SoftwareReencode,
    }

    /// <summary>H.264 bitrate ladder by OUTPUT height (video / maxrate / bufsize).
    /// Used by every re-encode plan so a 720p stream isn't shipped at 4K
    /// bitrate (and vice-versa). Conservative-ish for LAN delivery.</summary>
    private static (string V, string Max, string Buf) RateForHeight(int h) => h switch
    {
        <= 0    => ("5M",     "6500k",  "10M"),   // unknown → 1080-ish default
        <= 480  => ("1200k",  "1600k",  "2400k"),
        <= 576  => ("1800k",  "2400k",  "3600k"),
        <= 720  => ("2800k",  "3600k",  "5600k"),
        <= 1080 => ("5M",     "6500k",  "10M"),
        <= 1440 => ("9M",     "11M",    "18M"),
        _       => ("16M",    "20M",    "32M"),   // 2160p+
    };

    /// <summary>Pick the playback plan from the probe + detected hardware.
    ///   • H.264 (any bit depth) → MPEG-TS stream-copy. Works without any GPU.
    ///   • HEVC + NVENC present  → NVIDIA path (handles 8 AND 10-bit decode).
    ///   • HEVC 8-bit + VAAPI    → AMD/Intel path (can encode 8-bit).
    ///   • HEVC 10-bit, no NVENC → fMP4 stream-copy fallback.</summary>
    private HlsPlan ChoosePlan(ProbeInfo probe, int maxHeight = 0)
    {
        // Quality-ladder downscale: the user picked a resolution below the
        // source. Downscaling REQUIRES a re-encode (a stream-copy can't be
        // shrunk), so pick the best available encoder regardless of source
        // codec/bit-depth:
        //   NVENC  — decodes H.264 + HEVC 8/10-bit, encodes H.264. Preferred.
        //   VAAPI  — Vega/Intel decode H.264 + HEVC (incl. 10-bit on VCN); we
        //            encode H.264 8-bit so the Main10-ENCODE limitation doesn't
        //            apply. HDR is flattened to SDR (acceptable for a smaller
        //            rung; proper tonemap is a later refinement).
        //   libx264 — CPU fallback when no GPU encoder is present.
        if (maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height)
        {
            if (_hardware?.Current.Nvenc.Available == true) return HlsPlan.Fmp4NvencReencode;
            if (_hardware?.Current.Vaapi.Available == true) return HlsPlan.Fmp4VaapiReencode;
            return HlsPlan.Fmp4SoftwareReencode;
        }

        // No downscale → original codec-driven choice.
        if (string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            return HlsPlan.TsStreamCopy;

        // HEVC or anything else — need a HW path or fall back to stream-copy.
        // NVENC first (handles HEVC 10-bit Main10 decode, which VAAPI on Vega
        // can't); then VAAPI for 8-bit only; finally stream-copy.
        if (string.Equals(probe.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            if (_hardware?.Current.Nvenc.Available == true)
                return HlsPlan.Fmp4NvencReencode;
            if (!probe.Is10Bit && _hardware?.Current.Vaapi.Available == true)
                return HlsPlan.Fmp4VaapiReencode;
        }
        return HlsPlan.Fmp4StreamCopy;
    }

    /// <summary>Audio codecs decoded natively by every MSE-capable browser
    /// inside an MPEG-TS HLS stream. Conservative — AC3/E-AC3 work in
    /// Chrome but not Firefox, and DTS/TrueHD/Atmos work nowhere; those
    /// all fall through to AAC re-encoding. AAC + MP3 is the safe universe
    /// for "the user's browser will definitely decode this passthrough".</summary>
    private static bool IsBrowserCompatibleAudioInTs(string? codec) => (codec ?? "").ToLowerInvariant() switch
    {
        "aac" or "mp3" or "mp2" => true,
        _ => false,
    };

    private static string BuildMasterPlaylist(ProbeInfo p)
    {
        // BANDWIDTH is a rough estimate based on resolution. hls.js requires
        // the attribute to exist; it doesn't have to be exact.
        var bandwidth = EstimatedBandwidth(p.Width, p.Height);
        var codecs = p.CodecsAttribute ?? "avc1.640028,mp4a.40.2";
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n");
        sb.Append($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={p.Width}x{p.Height},CODECS=\"{codecs}\"\n");
        sb.Append("media.m3u8\n");
        return sb.ToString();
    }

    private static string BuildMediaPlaylist(double totalDuration, int segCount, HlsPlan plan,
        double startOffsetSec = 0, IReadOnlyCollection<int>? restartBoundaries = null)
    {
        // TARGETDURATION must be >= every #EXTINF value (HLS spec). We round
        // up the segment length to be safe.
        var target = (int)Math.Ceiling(SegmentDurationSec) + 1;

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        // VERSION 7 needed for fMP4 (#EXT-X-MAP). MPEG-TS works at 3 and is the
        // version Plex/Jellyfin/Emby emit — keeps maximum smart-TV compat.
        sb.Append(plan == HlsPlan.TsStreamCopy ? "#EXT-X-VERSION:3\n" : "#EXT-X-VERSION:7\n");
        sb.Append($"#EXT-X-TARGETDURATION:{target}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (startOffsetSec > 0)
            sb.Append($"#EXT-X-START:TIME-OFFSET={startOffsetSec.ToString("F3", CultureInfo.InvariantCulture)},PRECISE=YES\n");
        // MPEG-TS segments are self-contained (each carries its own PAT/PMT and
        // codec bootstrap data) — no init segment, no EXT-X-MAP. fMP4 needs
        // both. This is the whole reason MPEG-TS is so robust across decoders.
        if (plan != HlsPlan.TsStreamCopy)
            sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");

        // EXT-X-DISCONTINUITY policy is plan-specific:
        //
        // • fMP4 (`Fmp4*`): emit BEFORE EVERY segment after seg-0. Reason:
        //   seek-restart spawns a fresh ffmpeg whose encoder may emit
        //   slightly different codec parameters (SPS/PPS, SAR bytes, etc.).
        //   The fMP4 TFDT box bug also means new-encoder segments may have
        //   the wrong fragment start time. hls.js detects mid-segment drift
        //   and reloading the decoder produces the ~200ms audio-ahead
        //   wobble. Pre-emptive markers tell hls.js to plan for a fresh
        //   decoder at every boundary — when codec params match (the
        //   common case within one run), the decoder doesn't actually
        //   reload, cost is well under 1 ms per segment.
        //
        // • MPEG-TS (`TsStreamCopy`): emit ONLY at actual restart points.
        //   Reason: TS carries absolute time via PCR in every packet, and
        //   stream-copy from a single ffmpeg run produces monotonically
        //   increasing PCR with no genuine discontinuity. Marking every
        //   segment as discontinuous makes hls.js EXPECT a PCR reset at
        //   each boundary, see that it doesn't happen, and stall trying to
        //   resync. This is what was causing the "plays 2 sec then freezes
        //   solid" bug. Plex/Jellyfin/Emby's TS playlists confirm: they
        //   only emit DISCONTINUITY at real codec/seek transitions.
        var boundarySet = restartBoundaries is { Count: > 0 }
            ? new HashSet<int>(restartBoundaries)
            : null;
        bool emitDiscontinuityEverywhere = plan != HlsPlan.TsStreamCopy;

        var ext = SegmentExtension(plan);
        var remaining = totalDuration;
        for (int i = 0; i < segCount; i++)
        {
            bool emitMarker = i > 0 && (emitDiscontinuityEverywhere
                                     || (boundarySet?.Contains(i) ?? false));
            if (emitMarker) sb.Append("#EXT-X-DISCONTINUITY\n");
            var dur = Math.Min(SegmentDurationSec, remaining);
            sb.Append($"#EXTINF:{dur.ToString("F3", CultureInfo.InvariantCulture)},\n");
            sb.Append($"seg-{i:D5}.{ext}\n");
            remaining -= SegmentDurationSec;
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>Atomically rewrite media.m3u8 with the current set of restart
    /// boundaries baked in as EXT-X-DISCONTINUITY markers. Atomic so a
    /// concurrent player fetch can never see a half-written playlist.</summary>
    private void RegenerateMediaPlaylist(HlsSession session, double totalDurationForPlaylist)
    {
        var path = Path.Combine(session.OutputDir, "media.m3u8");
        var tmp  = path + ".tmp";
        try
        {
            var content = BuildMediaPlaylist(totalDurationForPlaylist, session.SegmentCount,
                session.Plan,
                startOffsetSec: session.SeekSec,
                restartBoundaries: session.GetRestartBoundaries());
            File.WriteAllText(tmp, content, Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HLS: media.m3u8 regeneration failed for session {Token}", session.Token);
        }
    }

    private static int EstimatedBandwidth(int width, int height)
    {
        // VBR HEVC very rough table — exact numbers don't matter for single-
        // variant streams, hls.js just wants the field present.
        long pixels = (long)Math.Max(width, 1) * Math.Max(height, 1);
        if (pixels >= 3840L * 2160) return 25_000_000;
        if (pixels >= 1920L * 1080) return 8_000_000;
        if (pixels >= 1280L *  720) return 4_000_000;
        return 2_000_000;
    }

    // ─── ffmpeg argv ─────────────────────────────────────────────────────────

    /// <summary>Build the ffmpeg command line for a session/restart. The plan
    /// determines container (TS vs fMP4), encoding (stream-copy vs VAAPI), and
    /// audio handling (passthrough vs AAC re-encode + itsoffset).</summary>
    private static List<string> BuildFfmpegArgs(
        string fullPath, double seekSec, string sessionDir, string mediaPlaylist,
        string masterPlaylistName, ProbeInfo? probe, HlsPlan plan,
        int startNumber = 0, bool reuseInit = false,
        int targetHeight = 0,
        double audioOffsetSec = 0.04,
        // Audio stream index inside the source (0 = first). Threaded through
        // to each Build*Args branch which substitutes it in the `-map` arg.
        // When externalAudioPath is set, this is the index WITHIN the external
        // file instead (almost always 0 — dub files carry a single stream).
        int audioTrackIndex = 0,
        // External sideload audio file. When non-null, every plan takes it as a
        // SECOND ffmpeg input and maps its audio (`-map 1:a:{audioTrackIndex}`)
        // — re-encoded to browser-safe AAC — instead of the source's own audio.
        // The video pipeline (TS copy / fMP4 / re-encode) is unchanged.
        string? externalAudioPath = null)
    {
        var args = new List<string>();
        var videoCodec = probe?.VideoCodec;
        var audioCodec = probe?.AudioCodec;

        // PTS positioning rules (applies to fMP4 paths; TS doesn't need
        // -copyts because PCR carries absolute time inside each segment):
        //   • Initial run (startNumber=0): default rebase-to-0 is correct.
        //   • Restart (startNumber=K): we need output PTS to start at
        //     K*segDur or fMP4's TFDT box claims time 0 while the playlist
        //     says seg-K starts at K*6s, and hls.js drops the segment as a
        //     duplicate/discontinuity. -copyts is the only flag the HLS
        //     muxer actually respects for this.

        switch (plan)
        {
            case HlsPlan.TsStreamCopy:
                BuildTsStreamCopyArgs(args, fullPath, seekSec, audioCodec, startNumber, audioTrackIndex,
                    externalAudioPath, audioOffsetSec);
                break;
            case HlsPlan.Fmp4VaapiReencode:
                BuildFmp4VaapiArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath);
                break;
            case HlsPlan.Fmp4NvencReencode:
                BuildFmp4NvencArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath);
                break;
            case HlsPlan.Fmp4StreamCopy:
                BuildFmp4StreamCopyArgs(args, fullPath, seekSec, startNumber, videoCodec, audioOffsetSec, audioTrackIndex,
                    externalAudioPath);
                break;
            case HlsPlan.Fmp4SoftwareReencode:
                BuildFmp4SoftwareArgs(args, fullPath, seekSec, audioOffsetSec, startNumber,
                    targetHeight, probe?.Height ?? 0, audioTrackIndex, externalAudioPath);
                break;
        }

        // ── HLS muxer options ────────────────────────────────────────────
        args.AddRange(new[]
        {
            "-f", "hls",
            "-hls_time", SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture),
            "-hls_segment_type", plan == HlsPlan.TsStreamCopy ? "mpegts" : "fmp4",
            // Sliding window: keep the last 50 segments physically on disk
            // (= ~5 min of content). Old ones get deleted via the
            // delete_segments flag below. WITHOUT this every UHD HEVC
            // session would fill the 8GB tmpfs in ~7 min and choke ffmpeg.
            // Scrub-back past the deleted window triggers our seek-restart
            // (RestartFfmpegAtSegmentAsync), which re-produces the needed
            // range from source.
            "-hls_list_size", "50",
            "-hls_playlist_type", "vod",
        });
        if (startNumber > 0)
        {
            args.Add("-start_number");
            args.Add(startNumber.ToString(CultureInfo.InvariantCulture));
        }
        var segExt = SegmentExtension(plan);
        args.AddRange(new[]
        {
            // delete_segments: physically remove segment files that fall off
            // the sliding window. Without this `-hls_list_size 50` only
            // trims the m3u8 (which we ignore — we have our own playlist),
            // leaving disk usage unbounded. WITH it, ffmpeg actively reclaims
            // tmpfs as it goes.
            "-hls_flags", "temp_file+delete_segments",
            "-hls_segment_filename", Path.Combine(sessionDir, $"seg-%05d.{segExt}"),
        });
        // fMP4 needs an init segment carrying decoder config; MPEG-TS is
        // self-bootstrapping (PAT/PMT/SPS in every segment).
        if (plan != HlsPlan.TsStreamCopy)
        {
            // For a restart, init.mp4 already exists from the original run.
            // Writing it again creates a race window where the file briefly
            // disappears (temp_file rename) and any concurrent fetch sees a
            // 0-byte response. Use a per-run sentinel filename to avoid
            // stomping the working init.
            var initName = reuseInit ? "_init_restart.mp4" : "init.mp4";
            args.AddRange(new[] { "-hls_fmp4_init_filename", initName });
        }
        args.AddRange(new[]
        {
            "-master_pl_name", masterPlaylistName,
            mediaPlaylist,
        });
        return args;
    }

    /// <summary>MPEG-TS HLS with stream-copy video and audio passthrough where
    /// possible. This is the Jellyfin/Emby/Plex Direct Stream path — works for
    /// every H.264 file (the most common case by far) with zero GPU usage and
    /// rock-solid A/V sync because the PCR field in each TS packet carries
    /// absolute clock-time the player can chase. Audio gets AAC re-encoded
    /// when the source codec isn't browser-friendly (DTS, TrueHD, etc.).</summary>
    private static void BuildTsStreamCopyArgs(List<string> args, string fullPath, double seekSec,
        string? audioCodec, int startNumber, int audioTrackIndex,
        string? externalAudioPath = null, double audioOffsetSec = 0.0)
    {
        bool ext = externalAudioPath is not null;

        // Input #0 — source. Video always; its own audio too unless an external
        // dub replaces it.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — external dub audio (a second input), sync-shifted via
        // -itsoffset exactly like the fMP4 paths. The TS path is normally
        // single-input, so this is only wired in when a dub is selected.
        if (ext)
        {
            if (seekSec > 0)
            {
                args.Add("-ss");
                args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
            }
            args.Add("-itsoffset");
            args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(externalAudioPath!);
        }

        // -copyts so the output PCR carries the SOURCE position (= K*segDur
        // for a seg-K restart) instead of being rebased to 0. Without this,
        // a resume/scrub-restart writes seg-K with PCR=0 while the playlist
        // claims seg-K is at K*6s timeline — hls.js sees the mismatch and
        // either drops the segment or stalls. Only needed when startNumber>0
        // (we're not starting from seg-0); for fresh playback PCR=0 is
        // already correct.
        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", ext ? $"1:a:{audioTrackIndex}?" : $"0:a:{audioTrackIndex}?",
            "-c:v", "copy",
        });
        // Audio: an external dub is always AAC-transcoded — its codec is
        // unknown here (.mka can hold FLAC/AC3/DTS/…) so we can't risk a copy.
        // For the source's own audio, passthrough when the browser can decode
        // it inside MPEG-TS (AAC/MP3 only across all browsers; AC3/E-AC3 work
        // in Chrome but break Firefox/Safari, so those re-encode to AAC).
        if (!ext && IsBrowserCompatibleAudioInTs(audioCodec))
        {
            args.AddRange(new[] { "-c:a", "copy" });
        }
        else
        {
            args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "192k" });
        }
    }

    /// <summary>fMP4 HLS with VAAPI re-encode to H.264 + B-frames disabled.
    /// Used for HEVC 8-bit on hosts with /dev/dri available. Combined with
    /// -itsoffset audio-sync compensation this is our pixel-perfect path.</summary>
    private static void BuildFmp4VaapiArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int audioTrackIndex,
        string? externalAudioPath = null)
    {
        // Re-encoding the video stream eliminates the B-frame display reorder
        // that puts the first reorderable frame ~83ms past the segment
        // boundary in stream-copy output. With -bf 0 + IDR-per-segment GOP we
        // get genuinely byte-aligned A/V at every segment edge.
        //
        // We additionally nudge audio later by `audioOffsetSec` (default 40ms,
        // overrideable per-client via the calibration result the browser
        // computes once on first play). Humans tolerate audio behind the
        // picture (~125ms threshold) much better than ahead (~45ms), so we
        // always err on the late side. Implementation: a second ffmpeg input
        // with -itsoffset — the only PTS-shift mechanism the HLS fMP4 muxer
        // respects (filter-graph adelay/aresample get re-aligned away).
        args.Add("-vaapi_device");
        args.Add("/dev/dri/renderD128");
        args.Add("-hwaccel"); args.Add("vaapi");
        args.Add("-hwaccel_output_format"); args.Add("vaapi");

        // Input #0 — video (GPU-decoded).
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio only, sync-offset baked in via -itsoffset. Software
        // demuxes audio so we don't fight VAAPI's audio path. When an external
        // dub is selected this input points at THAT file instead of the source
        // (its audio is mapped below); the same -ss/-itsoffset apply.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        var vf = "format=nv12|vaapi,hwupload";
        if (targetHeight > 0)
            vf = $"scale_vaapi=w=-2:h={targetHeight}:format=nv12,{vf}";
        var rate = RateForHeight(targetHeight);
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_vaapi",
            "-profile:v", "main",
            "-bf",     "0",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS with NVIDIA NVENC re-encode to H.264 + B-frames
    /// disabled. Used when NVENC is available (any RTX / GTX 10-series+).
    /// Unlike VAAPI on Vega, NVENC can decode AND encode HEVC Main10 →
    /// this plan is the preferred re-encode path for HDR/DV content on
    /// NVIDIA hosts. -bf 0 still applied so B-frame reorder doesn't
    /// reintroduce the fMP4 TFDT sync wobble.</summary>
    private static void BuildFmp4NvencArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int audioTrackIndex,
        string? externalAudioPath = null)
    {
        // CUDA-backed decode + NVENC encode. `-hwaccel_output_format cuda`
        // keeps frames on the GPU between decode and encode, avoiding a
        // CPU↔GPU bounce. `auto` lets ffmpeg pick the right NVDEC for the
        // source codec (h264_nvdec, hevc_nvdec, etc.).
        args.Add("-hwaccel"); args.Add("cuda");
        args.Add("-hwaccel_output_format"); args.Add("cuda");

        // Input #0 — video, GPU-decoded.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset (same trick as VAAPI/SW paths).
        // Points at the external dub file when one is selected.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        // Optional scale on GPU via scale_cuda. nv12 output format for
        // NVENC compatibility.
        var vf = targetHeight > 0
            ? $"scale_cuda=w=-2:h={targetHeight}:format=nv12"
            : "scale_cuda=format=nv12";
        var rate = RateForHeight(targetHeight);
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_nvenc",
            "-preset", "p4",          // p1=fastest, p7=highest quality; p4 is balanced
            "-tune", "hq",
            "-profile:v", "main",
            "-bf",     "0",           // no B-frames → tight A/V sync per segment
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS via SOFTWARE libx264 re-encode + optional downscale.
    /// The CPU fallback for the quality-ladder when no GPU encoder is present.
    /// Software-decodes the source (so it handles any codec incl. HEVC 10-bit),
    /// scales with the CPU `scale` filter, and encodes H.264 8-bit (yuv420p) at
    /// the ladder bitrate. -bf 0 + IDR-per-segment matches the GPU paths' A/V
    /// sync. veryfast preset to give the best shot at real-time on a CPU; if the
    /// box still can't keep up, that's on the operator (don't pick a lower rung
    /// than the source). HDR is flattened to SDR (no tonemap) — fine for a
    /// downscaled rung on a small screen.</summary>
    private static void BuildFmp4SoftwareArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int sourceHeight, int audioTrackIndex,
        string? externalAudioPath = null)
    {
        // Input #0 — video (software decode).
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset (same A/V-sync trick as the GPU paths).
        // Points at the external dub file when one is selected.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        var outH = targetHeight > 0 ? targetHeight : sourceHeight;
        var rate = RateForHeight(outH);
        var vf = targetHeight > 0 ? $"scale=-2:{targetHeight}" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-profile:v", "main",
            "-pix_fmt", "yuv420p",    // 10-bit/HDR source → 8-bit SDR H.264
            "-bf",     "0",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS with full stream-copy. Used for 10-bit HEVC HDR / DV
    /// where Vega 11 can't encode Main10. B-frames stay in the bitstream
    /// → first decoded sample's TFDT in fMP4 is the IDR's decode time but
    /// its display time is later, so audio reaches the user ~50ms before
    /// video. Compensate by shifting audio via -itsoffset on a separate
    /// input — same trick the VAAPI path uses. Without this knob the user
    /// has to rely on DLNA cast / mpv to avoid the wobble.</summary>
    private static void BuildFmp4StreamCopyArgs(List<string> args, string fullPath, double seekSec,
        int startNumber, string? videoCodec, double audioOffsetSec, int audioTrackIndex,
        string? externalAudioPath = null)
    {
        // A second input is needed either to shift the source's own audio
        // (-itsoffset) OR to pull audio from an external dub file. The external
        // case forces dual-input regardless of offset.
        bool ext          = externalAudioPath is not null;
        bool useDualInput = audioOffsetSec != 0.0 || ext;

        // Input #0 — video (and audio when not using a second input)
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset. The source file itself for a pure
        // sync nudge, or the external dub file when one is selected.
        if (useDualInput)
        {
            if (seekSec > 0)
            {
                args.Add("-ss");
                args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
            }
            args.Add("-itsoffset");
            args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(externalAudioPath ?? fullPath);
        }
        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", useDualInput ? $"1:a:{audioTrackIndex}?" : $"0:a:{audioTrackIndex}?",
            "-c:v", "copy",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
        if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-tag:v"); args.Add("hvc1");
        }
    }

    // ─── ffprobe ────────────────────────────────────────────────────────────

    private async Task<ProbeInfo?> ProbeMediaAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
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
                "-show_format",
                "-show_streams",
                fullPath,
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var durEl)
                && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                duration = d;

            string? vCodec = null;
            string? aCodec = null;
            int width = 1280, height = 720;
            bool hasDv = false;
            bool is10Bit = false;
            string? colorTransfer = null;   // smpte2084 → HDR10, arib-std-b67 → HLG
            int audioChannels = 0;
            string? audioLanguage = null;
            string? videoCodecsAttr = null;
            string? audioCodecsAttr = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (!s.TryGetProperty("codec_type", out var ct2)) continue;
                    var type = ct2.GetString();
                    if (type == "video" && vCodec is null)
                    {
                        if (s.TryGetProperty("codec_name", out var cn)) vCodec = cn.GetString();
                        if (s.TryGetProperty("width",  out var w) && w.TryGetInt32(out var wi)) width  = wi;
                        if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi)) height = hi;
                        // Capture color transfer characteristic so BuildOutputInfo
                        // can mark the stream as HDR10 / HLG. Distinct from
                        // bit-depth — 10-bit alone isn't HDR.
                        if (s.TryGetProperty("color_transfer", out var ctEl))
                            colorTransfer = ctEl.GetString();
                        // pix_fmt yuv420p10le / yuv444p10le etc. — anything containing
                        // "10le" or "10be" is 10-bit. Profile strings like "Main 10"
                        // are also a tell.
                        if (s.TryGetProperty("pix_fmt", out var pf))
                        {
                            var pix = pf.GetString() ?? "";
                            if (pix.Contains("10le", StringComparison.OrdinalIgnoreCase)
                             || pix.Contains("10be", StringComparison.OrdinalIgnoreCase))
                                is10Bit = true;
                        }
                        if (s.TryGetProperty("profile", out var pr))
                        {
                            var prof = pr.GetString() ?? "";
                            if (prof.Contains("Main 10", StringComparison.OrdinalIgnoreCase))
                                is10Bit = true;
                        }
                        if (s.TryGetProperty("side_data_list", out var sd))
                        {
                            foreach (var sdEl in sd.EnumerateArray())
                                if (sdEl.TryGetProperty("side_data_type", out var sdt)
                                    && (sdt.GetString() ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                                    hasDv = true;
                        }
                        videoCodecsAttr = BuildVideoCodecsAttribute(vCodec, s);
                    }
                    else if (type == "audio" && aCodec is null)
                    {
                        // Capture the FIRST audio stream's codec name. ChoosePlan
                        // uses this to decide passthrough vs AAC re-encode on the
                        // MPEG-TS branch. We still default the MSE CODECS=… string
                        // to mp4a.40.2 because the fMP4 path always transcodes;
                        // the TS branch sidesteps the master playlist's codecs
                        // attribute entirely (browsers infer from segment bytes).
                        if (s.TryGetProperty("codec_name", out var acn)) aCodec = acn.GetString();
                        if (s.TryGetProperty("channels",   out var ach) && ach.TryGetInt32(out var ci))
                            audioChannels = ci;
                        // Language tag: ffprobe puts it under tags.language, but
                        // MKV-from-Matroska sometimes uses LANGUAGE (uppercase).
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("language", out var langEl))
                                audioLanguage = langEl.GetString();
                            else if (tags.TryGetProperty("LANGUAGE", out var langElU))
                                audioLanguage = langElU.GetString();
                        }
                        audioCodecsAttr = "mp4a.40.2";
                    }
                }
            }

            var combined = (videoCodecsAttr, audioCodecsAttr) switch
            {
                (string v, string a) => $"{v},{a}",
                (string v, null)     => v,
                (null,     string a) => a,
                _                    => null,
            };

            return new ProbeInfo(duration, vCodec, aCodec, width, height, combined, hasDv, is10Bit,
                colorTransfer, audioChannels, audioLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", fullPath);
            return null;
        }
    }

    private static string? BuildVideoCodecsAttribute(string? codecName, JsonElement stream)
    {
        // Build a `CODECS=…` string MSE can match. For HEVC we always re-tag
        // to hvc1 in ffmpeg args, so the attribute must use hvc1 too.
        if (string.Equals(codecName, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            // hvc1.{profile}.{compat}.L{level}.{constraints}
            // Defaults match Main10 L5.0 — the most common 4K profile.
            int profile = 2; // 1=Main, 2=Main10
            if (stream.TryGetProperty("profile", out var p))
            {
                var pn = p.GetString() ?? "";
                if (pn.Contains("Main 10", StringComparison.OrdinalIgnoreCase)) profile = 2;
                else if (pn.Contains("Main", StringComparison.OrdinalIgnoreCase)) profile = 1;
            }
            int level = 150;
            if (stream.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
                level = lvl;
            return $"hvc1.{profile}.4.L{level}.B0";
        }
        if (string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase))
        {
            // avc1.{profile_idc:x2}{constraint:x2}{level_idc:x2}
            int profileIdc = 0x64;  // 100 = High
            int levelIdc   = 0x28;  // 40 = level 4.0
            if (stream.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
                levelIdc = lvl;
            return $"avc1.{profileIdc:X2}00{levelIdc:X2}".ToLowerInvariant();
        }
        if (string.Equals(codecName, "av1", StringComparison.OrdinalIgnoreCase))
            return "av01.0.05M.08"; // Main profile, level 5.0, 8-bit
        if (string.Equals(codecName, "vp9", StringComparison.OrdinalIgnoreCase))
            return "vp09.00.50.08";
        return null;
    }

    // ─── GC ──────────────────────────────────────────────────────────────────

    private void GarbageCollect()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (token, session) in _sessions)
        {
            if (session.LastActive < cutoff)
            {
                _logger.LogInformation("Reaping idle HLS session {Token} (last active {Age:F0}s ago)",
                    token, (DateTime.UtcNow - session.LastActive).TotalSeconds);
                Stop(token);
                continue;
            }

            // Crash detection: ffmpeg exited with non-zero. Player will get
            // 503s on every segment, so kill the session decisively rather
            // than letting it linger another 5 minutes.
            if (session.Process.HasExited && session.Process.ExitCode != 0)
            {
                _logger.LogInformation("Reaping crashed HLS session {Token} (ffmpeg exit {Code})", token, session.Process.ExitCode);
                Stop(token);
                continue;
            }

            // Incomplete-exit detection: ffmpeg exited successfully but only
            // produced part of the segments we expected. This shouldn't
            // happen with VOD encoding to completion, but can happen if our
            // probe under-counted duration vs ffmpeg's interpretation. Don't
            // tear it down (player can still seek into what exists) — just
            // note it for log-diving.
            if (session.Process.HasExited && session.Process.ExitCode == 0
                && session.SegmentCount > 0)
            {
                var produced = HighestProducedSegment(session.OutputDir) + 1;
                if (produced < session.SegmentCount)
                {
                    _logger.LogDebug("HLS {Token}: ffmpeg done at seg-{Produced}/{Total} — partial encoding",
                        token, produced, session.SegmentCount);
                }
            }
        }

        // Sweep orphan tmp dirs (a previous process or crash may have left
        // some behind). Anything under _rootDir not matching an active token
        // is fair game.
        try
        {
            var activeTokens = _sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(_rootDir))
            {
                var name = Path.GetFileName(dir);
                if (activeTokens.Contains(name)) continue;
                // Only delete dirs older than 2 min so we don't race a
                // freshly-created session whose token hasn't reached the
                // dictionary yet.
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((DateTime.UtcNow - info.CreationTimeUtc).TotalMinutes < 2) continue;
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("HLS: swept orphan session dir {Dir}", name);
                }
                catch { /* best-effort */ }
            }
        }
        catch { }
    }

    /// <summary>Diagnostic snapshot of every live session for /api/hls/sessions.</summary>
    public IReadOnlyList<HlsSessionStatus> Snapshot()
    {
        return _sessions.Select(kv =>
        {
            var s = kv.Value;
            var produced = HighestProducedSegment(s.OutputDir) + 1;
            var exited   = s.Process.HasExited;
            return new HlsSessionStatus(
                Token:           kv.Key,
                SourcePath:      s.SourcePath,
                StartSeekSec:    s.SeekSec,
                SegmentsTotal:   s.SegmentCount,
                SegmentsReady:   Math.Max(produced, 0),
                IdleSec:         (DateTime.UtcNow - s.LastActive).TotalSeconds,
                FfmpegExited:    exited,
                FfmpegExitCode:  exited ? s.Process.ExitCode : (int?)null
            );
        }).ToArray();
    }

    public sealed record HlsSessionStatus(
        string Token, string SourcePath, double StartSeekSec,
        int SegmentsTotal, int SegmentsReady, double IdleSec,
        bool FfmpegExited, int? FfmpegExitCode);

    private static void TryRemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _gcTimer.Dispose();
        foreach (var token in _sessions.Keys.ToList())
            Stop(token);
    }

    // ─── Per-session record ──────────────────────────────────────────────────

    private sealed class HlsSession
    {
        public string Token       { get; }
        public string SourcePath  { get; }
        public string OutputDir   { get; }
        public Process Process    { get; private set; }
        public double  SeekSec    { get; }
        public int     SegmentCount { get; }
        public string? VideoCodec { get; }
        public HlsPlan Plan       { get; }
        public double  AudioOffsetSec { get; }
        public double  TotalDurationSec { get; }
        // Which audio stream of the source we selected via `-map 0:a:{N}?`.
        // Set once at session creation; the session restart logic (used for
        // backward scrub jumps) needs to keep mapping the same audio track.
        public int     AudioTrackIndex { get; }
        // Output height cap (0 = native). Carried so the seek-restart re-runs
        // ffmpeg with the same downscale instead of reverting to source res.
        public int     MaxHeight  { get; }
        // External sideload audio file (null = use the source's own audio).
        // Carried so a backward-scrub restart keeps muxing the same dub track
        // instead of silently reverting to the source audio.
        public string? ExternalAudioPath { get; }
        public DateTime CreatedAt  { get; }
        public DateTime LastActive { get; private set; }

        // Segments at which a NEW ffmpeg process took over. Used by
        // RegenerateMediaPlaylist to insert EXT-X-DISCONTINUITY markers
        // so hls.js does a clean decoder reset instead of trying to
        // continue the previous run's pipeline through what's actually
        // a fresh encode (with potentially different SPS/PPS bytes).
        private readonly HashSet<int> _restartBoundaries = new();
        private readonly object _boundaryLock = new();

        public void AddRestartBoundary(int segmentIndex)
        {
            lock (_boundaryLock) { _restartBoundaries.Add(segmentIndex); }
        }
        public IReadOnlyCollection<int> GetRestartBoundaries()
        {
            lock (_boundaryLock) { return _restartBoundaries.ToArray(); }
        }

        public HlsSession(string token, string source, string dir, Process proc, double seekSec, int segCount,
            string? videoCodec, HlsPlan plan, double audioOffsetSec, double totalDurationSec,
            int audioTrackIndex, int maxHeight, string? externalAudioPath = null)
        {
            Token       = token;
            SourcePath  = source;
            OutputDir   = dir;
            Process     = proc;
            SeekSec     = seekSec;
            SegmentCount = segCount;
            VideoCodec  = videoCodec;
            Plan        = plan;
            AudioOffsetSec = audioOffsetSec;
            TotalDurationSec = totalDurationSec;
            AudioTrackIndex  = audioTrackIndex;
            MaxHeight   = maxHeight;
            ExternalAudioPath = externalAudioPath;
            CreatedAt   = DateTime.UtcNow;
            LastActive  = DateTime.UtcNow;
        }

        public void Touch() => LastActive = DateTime.UtcNow;

        /// <summary>Replace the ffmpeg process after a seek-restart. The old
        /// process must already be terminated; we dispose it here.</summary>
        public void SwapProcess(Process newProc)
        {
            var old = Process;
            Process = newProc;
            try { old.Dispose(); } catch { }
        }
    }
}
