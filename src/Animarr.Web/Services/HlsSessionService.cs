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
public sealed partial class HlsSessionService : IDisposable
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
    // 4s (was 6s) so the FIRST playable segment exists ~33% sooner → faster
    // startup. All segment math (playlist EXTINF, segCount, seek-restart, the
    // -force_key_frames GOP expr) derives from this constant, so they stay in
    // sync. 4s keeps per-segment overhead modest while shaving start latency.
    private const double SegmentDurationSec = 4.0;
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

    /// <summary>Live HLS transcode sessions right now (idle-but-not-yet-reaped
    /// included). Background jobs (trickplay) poll this to yield the CPU to
    /// playback.</summary>
    public int ActiveSessionCount => _sessions.Count;

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
    /// Returned from <see cref="ChoosePlaybackAsync"/>. Two native paths and an
    /// HLS fallback:
    ///   • <c>DirectPlay</c> — raw file via /api/file (Range-seekable). MP4 +
    ///     browser codec + AAC. Client sets src to <c>DirectUrl</c> directly.
    ///   • <c>DirectStream</c> — on-the-fly remux to progressive fMP4 via
    ///     /api/video (video stream-copy, audio→AAC). Non-MP4 containers (MKV)
    ///     whose video the browser decodes. Original video + HDR preserved;
    ///     plays on a real &lt;video&gt; (better HDR output than MSE). No Range,
    ///     so the client seeks by re-requesting at ?seek=N. <c>DirectUrl</c>
    ///     holds the /api/video URL.
    ///   • Neither — caller falls through to <see cref="StartAsync"/> (HLS).
    /// </summary>
    public sealed record PlaybackDecision(bool DirectPlay, string? DirectUrl, double DurationSec,
        PlayerOutputInfo? Output, bool DirectStream = false);

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
    public async Task<PlaybackDecision> ChoosePlaybackAsync(string fullPath,
        bool clientHevc = false, bool clientHevc10 = false, CancellationToken ct = default,
        IReadOnlySet<string>? nativeCaps = null)
    {
        var probe = await ProbeMediaAsync(fullPath, ct);
        var duration = probe?.DurationSec ?? 0;
        if (probe is null) return new PlaybackDecision(false, null, duration, null);

        var container = Path.GetExtension(fullPath).ToLowerInvariant().TrimStart('.');

        // ── Native client (Android TV ExoPlayer) — capability negotiation ──
        // The browser eligibility rules below are wrong for ExoPlayer: it
        // demuxes MKV/AVI/TS itself and the device usually decodes MPEG4-ASP,
        // AC3/E-AC3 etc. The client probes MediaCodecList and sends the token
        // list (containers + codecs); we serve the widest tier the DEVICE can
        // take instead of burning CPU on a transcode a browser would need.
        // Old AVI/XviD rips are the poster child: full re-encode for the web,
        // plain /api/file for the TV.
        if (nativeCaps is { Count: > 0 })
        {
            bool videoOk = NativeVideoOk(probe, nativeCaps);
            bool audioOk = NativeTokenOk(probe.AudioCodec, nativeCaps);
            bool containerOk = nativeCaps.Contains(container);

            if (videoOk && audioOk && containerOk)
            {
                var nativeUrl = "/api/file?path=" + Uri.EscapeDataString(fullPath);
                var nativeOut = BuildOutputInfo(probe, plan: null, isDirectPlay: true);
                return new PlaybackDecision(true, nativeUrl, duration, nativeOut);
            }
            if (videoOk && !probe.HasDolbyVision)
            {
                // Device decodes the video but not the audio (or can't demux the
                // container) → /api/video: video stream-copy + audio→AAC. Still
                // ~0% CPU compared to a full re-encode.
                var remuxUrl = "/api/video?path=" + Uri.EscapeDataString(fullPath);
                var remuxOut = BuildOutputInfo(probe, plan: null, isDirectPlay: false, isDirectStream: true);
                return new PlaybackDecision(false, remuxUrl, duration, remuxOut, DirectStream: true);
            }
            // Video codec beyond the device → HLS transcode as usual.
            return new PlaybackDecision(false, null, duration, null);
        }

        if (IsDirectPlayEligible(container, probe, clientHevc, clientHevc10))
        {
            // /api/file serves the raw bytes with Range support — exactly what
            // <video> needs for native seek. URL-escape the path so spaces and
            // unicode survive (file paths frequently have both).
            var directUrl = "/api/file?path=" + Uri.EscapeDataString(fullPath);
            var output = BuildOutputInfo(probe, plan: null, isDirectPlay: true);
            return new PlaybackDecision(true, directUrl, duration, output);
        }

        if (IsDirectStreamEligible(container, probe, clientHevc, clientHevc10))
        {
            // /api/video remuxes the source to progressive fMP4 (video copy,
            // audio→AAC). The browser plays it as a native <video>, which is
            // why we route MKV here instead of HLS: original video bitstream +
            // HDR are preserved, and native playback outputs HDR more reliably
            // than MSE. The client owns the seek-reload dance (?seek=N).
            var streamUrl = "/api/video?path=" + Uri.EscapeDataString(fullPath);
            var output = BuildOutputInfo(probe, plan: null, isDirectPlay: false, isDirectStream: true);
            return new PlaybackDecision(false, streamUrl, duration, output, DirectStream: true);
        }

        return new PlaybackDecision(false, null, duration, null);
    }

    /// <summary>
    /// Builds the <see cref="PlayerOutputInfo"/> the HUD reads off — the
    /// stream the player actually receives after our serving decision. For
    /// direct play and stream-copy paths it mirrors the source; for re-encode
    /// paths it reports the post-transcode codec/bit-depth/HDR state.
    /// </summary>
    private static PlayerOutputInfo BuildOutputInfo(ProbeInfo probe, HlsPlan? plan, bool isDirectPlay,
        bool isDirectStream = false)
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

        string container = isDirectPlay || isDirectStream ? "mp4"
                         : plan == HlsPlan.TsStreamCopy   ? "mpegts"
                         :                                  "fmp4";

        string planName = isDirectPlay   ? "directplay"
            : isDirectStream             ? "directstream"
            : plan switch
            {
                HlsPlan.TsStreamCopy         => "ts-copy",
                HlsPlan.Fmp4VaapiReencode    => "vaapi-reencode",
                HlsPlan.Fmp4NvencReencode    => "nvenc-reencode",
                HlsPlan.Fmp4SoftwareReencode => "sw-reencode",
                HlsPlan.Fmp4StreamCopy       => "fmp4-copy",
                _                            => "unknown",
            };

        string? reason = isDirectPlay || isDirectStream ? null : plan switch
        {
            HlsPlan.TsStreamCopy      => "H.264 source remuxed to MPEG-TS for HLS delivery",
            HlsPlan.Fmp4VaapiReencode => "HEVC 8-bit re-encoded to H.264 via VAAPI for browser compatibility (HDR lost)",
            HlsPlan.Fmp4NvencReencode => "HEVC re-encoded to H.264 via NVENC for browser compatibility (HDR lost)",
            HlsPlan.Fmp4SoftwareReencode => "Re-encoded to H.264 via CPU (libx264) for the requested quality (HDR lost)",
            HlsPlan.Fmp4StreamCopy    => "HEVC stream-copied to fMP4 — original bitstream, no re-encode (browser decodes HEVC; HDR/10-bit preserved)",
            _                          => null,
        };

        // Audio output reflects what the player actually receives, not the
        // source. Audio is copied as-is only on Direct Play (raw file) and the
        // TS path with a browser-native source codec; every other path — Direct
        // Stream, fMP4 copy/reencode, TS with incompatible audio — transcodes to
        // AAC stereo, so the Info block reports AAC 2.0 there, not e.g. TrueHD 7.1.
        bool audioCopied = isDirectPlay
            || (plan == HlsPlan.TsStreamCopy && IsBrowserCompatibleAudioInTs(probe.AudioCodec));

        return new PlayerOutputInfo(
            Plan:            planName,
            Container:       container,
            VideoCodec:      videoCodec,
            BitDepth:        bitDepth,
            Hdr:             hdr,
            HdrFormats:      hdrFormats.ToArray(),
            Width:           probe.Width,
            Height:          probe.Height,
            AudioCodec:      audioCopied ? (probe.AudioCodec ?? "") : "aac",
            AudioChannels:   audioCopied ? probe.AudioChannels      : 2,
            AudioLanguage:   probe.AudioLanguage ?? "",
            Transcoded:      !isDirectPlay && !isDirectStream,
            TranscodeReason: reason);
    }

    /// <summary>Video eligibility against the native client's capability tokens.
    /// 10-bit HEVC needs the explicit <c>hevc10</c> token; Dolby Vision is never
    /// offered natively (device DV handling is a minefield — HLS path knows how
    /// to strip/serve it). 10-bit H.264 (High10) has no hardware decoders on
    /// TV SoCs → transcode.</summary>
    private static bool NativeVideoOk(ProbeInfo probe, IReadOnlySet<string> caps)
    {
        if (probe.HasDolbyVision) return false;
        var vc = (probe.VideoCodec ?? "").ToLowerInvariant();
        return vc switch
        {
            "h264"  => !probe.Is10Bit && caps.Contains("h264"),
            "hevc"  => caps.Contains(probe.Is10Bit ? "hevc10" : "hevc"),
            "mpeg4" => caps.Contains("mpeg4"),
            "mpeg2" or "mpeg2video" => caps.Contains("mpeg2"),
            "vp9"   => caps.Contains("vp9"),
            "vp8"   => caps.Contains("vp8"),
            "av1"   => caps.Contains("av1"),
            _       => false,
        };
    }

    /// <summary>Audio token check with the ffprobe→token aliases the client
    /// derives from Android mime types.</summary>
    private static bool NativeTokenOk(string? codec, IReadOnlySet<string> caps)
    {
        var c = (codec ?? "").ToLowerInvariant();
        return c switch
        {
            "aac" or "mp4a"        => caps.Contains("aac"),
            "mp3" or "mp2"         => caps.Contains("mp3"),
            "ac3"                  => caps.Contains("ac3"),
            "eac3" or "eac3_joc"   => caps.Contains("eac3"),
            "dts"                  => caps.Contains("dts"),
            "truehd"               => caps.Contains("truehd"),
            "opus"                 => caps.Contains("opus"),
            "vorbis"               => caps.Contains("vorbis"),
            "flac"                 => caps.Contains("flac"),
            _ when c.StartsWith("pcm") => caps.Contains("pcm"),
            _                      => false,
        };
    }

    private static bool IsDirectPlayEligible(string container, ProbeInfo probe, bool clientHevc, bool clientHevc10)
    {
        // Native <video src> needs a browser-demuxable MP4-family container.
        // MKV isn't demuxable in-browser (→ HLS); .mov is fine in Safari/Chrome.
        if (container != "mp4" && container != "m4v" && container != "mov") return false;

        // Dolby Vision: browsers can't decode the DV layer → never native-play
        // (falls to HLS stream-copy / re-encode instead).
        if (probe.HasDolbyVision) return false;

        // Video must be something the browser decodes natively. Direct Play is
        // what gets the file onto a real <video> element — the only path where
        // client-GPU features (NVIDIA RTX VSR / Video HDR) engage; MSE/HLS
        // doesn't trigger them. HEVC is gated on the capability the client
        // reported: 8-bit → clientHevc, 10-bit (HDR10) → clientHevc10.
        var vc = (probe.VideoCodec ?? "").ToLowerInvariant();
        bool videoOk = vc switch
        {
            "h264" => !probe.Is10Bit,                              // 10-bit High10 isn't browser-safe
            "hevc" => probe.Is10Bit ? clientHevc10 : clientHevc,
            _      => false,
        };
        if (!videoOk) return false;

        // Audio: native playback can't transcode, so the track must be a codec
        // every browser decodes inside MP4. AC3/E-AC3 is spotty, DTS/TrueHD not
        // at all → those keep the file on the HLS path (audio re-encoded there).
        var ac = (probe.AudioCodec ?? "").ToLowerInvariant();
        if (ac != "aac" && ac != "mp3" && ac != "mp4a") return false;

        return true;
    }

    /// <summary>
    /// Direct Stream eligibility — the file the browser can't open as a raw
    /// &lt;video src&gt; (non-MP4 container, e.g. MKV/AVI) but whose VIDEO codec
    /// it CAN decode. /api/video remuxes it to progressive fMP4 (video copy,
    /// audio→AAC), so the only gate is the video codec/bit-depth vs the client's
    /// reported HEVC support. Audio codec is irrelevant (always transcoded),
    /// which makes this wider than Direct Play. Dolby Vision is allowed here
    /// (unlike Direct Play): the remux tags HEVC as hvc1 and DV profile 8.1
    /// plays as HDR10 — exactly the "keep the HDR" case we want native.
    /// </summary>
    private static bool IsDirectStreamEligible(string container, ProbeInfo probe,
        bool clientHevc, bool clientHevc10)
    {
        // Browser-native containers never come here: they're either Direct Play
        // (raw, Range-seekable) or HLS. Only non-native containers need a remux.
        if (container is "mp4" or "m4v" or "mov" or "webm") return false;

        var vc = (probe.VideoCodec ?? "").ToLowerInvariant();
        return vc switch
        {
            "h264" => !probe.Is10Bit,                              // 8-bit H.264 plays everywhere
            "hevc" => probe.Is10Bit ? clientHevc10 : clientHevc,   // gate on browser HEVC support
            _      => false,                                       // VP9/AV1/MPEG-2/… → HLS
        };
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
        string? externalAudioPath = null,
        // True when the client reported it can decode HEVC (via
        // MediaSource.isTypeSupported). Lets ChoosePlan ship HEVC as a
        // stream-copy (Direct Stream — original bitstream, no re-encode)
        // instead of transcoding it to H.264.
        bool clientHevc = false,
        // Cap output video bitrate in Mbps (player Bitrate menu). >0 forces a
        // re-encode at this bitrate; 0 = no cap. Carried on the session so
        // seek-restarts keep it (mirrors maxHeight).
        int maxBitrate = 0)
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
        var plan = probe is not null ? ChoosePlan(probe, maxHeight, clientHevc, maxBitrate) : HlsPlan.Fmp4StreamCopy;
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
        // fMP4 stream-copy keeps the source's B-frames, so video presentation
        // lags decode by the reorder delay while audio plays on time → audio
        // runs ahead. Pre-load the measured delay (from probe) as the baseline
        // itsoffset; the SW slider then trims on top. 0 for every other plan
        // (re-encode strips B-frames via -bf 0; TS carries PCR timing).
        var reorderDelaySec = plan == HlsPlan.Fmp4StreamCopy
            ? Math.Clamp(probe?.ReorderDelaySec ?? 0.0, 0.0, 1.0) : 0.0;
        var audioOffsetSec = reorderDelaySec + (externalAudioPath is not null
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
            });
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
            externalAudioPath: externalAudioPath, maxBitrate: maxBitrate);
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
            externalAudioPath, maxBitrate: maxBitrate);
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
                if (_sessions.TryRemove(token, out var dead))
                    try { dead.RestartLock.Dispose(); } catch { }
                TryRemoveDir(dir);
                throw new InvalidOperationException($"ffmpeg exited before producing init/first segment (code {proc.ExitCode}).");
            }
            await Task.Delay(150, ct);
        }
        // Phase B — short grace period for ONE more segment so the player has a
        // little buffer beyond seg-0. Cut from 5s/2-segments to 2s/1-segment:
        // with the loopback proxy + hls.js's generous forward buffer, returning
        // as soon as a second segment exists (or 2s, whichever first) starts
        // playback ~3-5s sooner without re-introducing the cold-cache stall the
        // old grace guarded against (hls.js keeps fetching ahead while playing).
        var secondSegPath = Path.Combine(dir, $"seg-{(startSegment + 1):D5}.{segExt}");
        var graceDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < graceDeadline)
        {
            if (File.Exists(secondSegPath)) break;
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

    private async Task RestartFfmpegAtSegmentAsync(HlsSession session, int targetSegment, CancellationToken ct)
    {
        // Serialise restarts so a flurry of player seeks (the seek bar fires
        // multiple `seeking` events as the user drags) can't spawn racing
        // ffmpegs that overwrite each other's output. The lock is PER SESSION —
        // a shared one made a seek in one playback stall the restart of every
        // other concurrent viewer.
        await session.RestartLock.WaitAsync(ct);
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
                externalAudioPath: session.ExternalAudioPath,
                maxBitrate: session.MaxBitrate);

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
            session.RestartLock.Release();
        }
    }

    public void Stop(string token)
    {
        if (!_sessions.TryRemove(token, out var s)) return;
        try { if (!s.Process.HasExited) s.Process.Kill(true); } catch { }
        try { s.Process.Dispose(); } catch { }
        try { s.RestartLock.Dispose(); } catch { }
        TryRemoveDir(s.OutputDir);
        _logger.LogInformation("HLS session {Token} stopped (by client / GC).", token);
    }

}
