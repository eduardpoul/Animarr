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
/// ffmpeg is paused once it is far enough ahead of the player, and segments
/// well behind the player are deleted (HlsSessionService.Throttle.cs), so a
/// session dir holds a bounded window, not the whole file.
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
    /// A household realistically has 1-3 (a player or two + maybe a background
    /// tab); anything past this is almost certainly leaked sessions from
    /// tab-close-without-detach, and each one ties up an ffmpeg + tmpfs dir, so
    /// at the cap a start evicts — its own client's sessions first, then idle
    /// ones (see <see cref="StartAsync"/>).</summary>
    private const int MaxConcurrentSessions = 5;

    /// <summary>A session touched (segment fetch / keepalive) within this
    /// window counts as someone's live playback and is never evicted for
    /// another client. The players ping every 30 s, even while paused, so this
    /// is three missed pings.</summary>
    private static readonly TimeSpan ActiveSessionGrace = TimeSpan.FromSeconds(90);

    private readonly ILogger<HlsSessionService> _logger;
    private readonly HardwareInfoService? _hardware;
    private readonly string _toneMapMode;
    private readonly MediaProbeCache _probeCache;
    private readonly MediaCachePaths? _cachePaths;
    private readonly KeyframeIndexService? _keyframes;
    private readonly IServiceScopeFactory? _scopes;
    private readonly bool _keyframeHlsDefault;
    private readonly SubtitleFontService? _fonts;
    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly Timer _gcTimer;
    private readonly Timer _throttleTimer;

    /// <summary>Live HLS transcode sessions right now (idle-but-not-yet-reaped
    /// included). Background jobs (trickplay) poll this to yield the CPU to
    /// playback.</summary>
    public int ActiveSessionCount => _sessions.Count;

    public HlsSessionService(ILogger<HlsSessionService> logger, HardwareInfoService? hardware = null,
        MediaProbeCache? probeCache = null,
        Microsoft.Extensions.Options.IOptions<Configuration.AppSettings>? appOptions = null,
        // Subtitle burn-in (burnSubtitle): the subtitle cache's root and the
        // attached-fonts extractor. Without them a burn request is refused.
        MediaCachePaths? cachePaths = null, SubtitleFontService? fonts = null,
        // Key-frame-exact copy segmentation (task 6.2): the index service and
        // a scope factory for the live Playback:KeyframeHls switch.
        KeyframeIndexService? keyframes = null, IServiceScopeFactory? scopes = null)
    {
        _logger  = logger;
        _keyframes = keyframes;
        _scopes = scopes;
        _keyframeHlsDefault = appOptions?.Value.KeyframeHls ?? true;
        _cachePaths = cachePaths;
        _fonts = fonts;
        _hardware = hardware;
        _toneMapMode = (appOptions?.Value.HdrToneMapping ?? "auto").Trim().ToLowerInvariant();
        _probeCache = probeCache
            ?? new MediaProbeCache(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaProbeCache>.Instance);
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
        // Look-ahead throttle + pruning of played segments — see
        // HlsSessionService.Throttle.cs for why the session dir needs both.
        _throttleTimer = new Timer(_ => ThrottleTick(), null, ThrottleInterval, ThrottleInterval);
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
    /// Who a session belongs to: the account (<paramref name="UserId"/>) and the
    /// client device (<paramref name="ClientId"/>). A new start replaces only
    /// sessions of the SAME owner — the "same player restarted with another
    /// track/quality" case — so the TV and a browser, or two profiles, can watch
    /// the same file side by side without killing each other's stream.
    /// </summary>
    public readonly record struct SessionOwner(string UserId, string ClientId)
    {
        /// <summary>For callers that can't say (none today) — they share one bucket.</summary>
        public static readonly SessionOwner Unknown = new("", "");

        public override string ToString()
            => $"user={(string.IsNullOrEmpty(UserId) ? "-" : UserId)} client={(string.IsNullOrEmpty(ClientId) ? "-" : ClientId)}";
    }

    /// <summary>Lowest / highest accepted <c>maxHeight</c> cap (0 = no cap).</summary>
    public const int MinMaxHeight = 144, MaxMaxHeight = 4320;

    /// <summary>
    /// Validates the quality / track knobs of a start request before anything
    /// is probed or stopped. Returns the error (code + message for a 400) or
    /// null, and the cap to actually use: an odd height is rounded DOWN to
    /// even — 4:2:0 encoders need even dimensions (libx264 aborts on
    /// <c>scale=-2:481</c>), so 721 means 720. The track index is checked
    /// against the real track count later, in <see cref="StartAsync"/>, once
    /// the file is probed.
    /// </summary>
    public static (string Code, string Message)? ValidateStartOptions(int audioTrackIndex, int maxHeight,
        int maxBitrate, out int effectiveMaxHeight)
    {
        effectiveMaxHeight = maxHeight & ~1;
        if (audioTrackIndex < 0)
            return (StartFailedException.InvalidAudioTrack, $"audioTrackIndex must be 0 or greater (got {audioTrackIndex}).");
        if (maxHeight < 0 || (maxHeight > 0 && (maxHeight < MinMaxHeight || maxHeight > MaxMaxHeight)))
            return (StartFailedException.InvalidMaxHeight,
                $"maxHeight must be 0 (no cap) or between {MinMaxHeight} and {MaxMaxHeight} (got {maxHeight}).");
        if (maxBitrate < 0)
            return (StartFailedException.InvalidMaxBitrate, $"maxBitrate must be 0 (no cap) or a positive Mbps value (got {maxBitrate}).");
        return null;
    }

    /// <summary>How long <see cref="StartAsync"/> waits for ffmpeg's first
    /// segment (+ init for fMP4). Generous on purpose: a cold-cache seek into a
    /// huge UHD MKV can take 10–30 s before the first flush.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <see cref="StartAsync"/> could not produce a playable session. Carries a
    /// stable machine-readable <see cref="Code"/> and the HTTP status the
    /// endpoint should answer with, so a client can tell "fix the request"
    /// (4xx) from "this plan failed on the server" (5xx) and recover — fall back
    /// to the previous stream or another plan — instead of waiting on a
    /// manifest that never fills.
    /// </summary>
    public sealed class StartFailedException(string code, int statusCode, string message) : Exception(message)
    {
        /// <summary>ffmpeg exited before writing the first segment (500).</summary>
        public const string FfmpegExited = "ffmpeg_exited";
        /// <summary>ffmpeg still running but no first segment within <see cref="StartupTimeout"/> (504).</summary>
        public const string FfmpegTimeout = "ffmpeg_timeout";
        /// <summary>At the session cap and every session is another client's live playback (503).</summary>
        public const string TooManySessions = "too_many_sessions";
        /// <summary>A newer start for the same file from the same client became
        /// playable first and replaced this still-starting one (409).</summary>
        public const string Superseded = "superseded";
        /// <summary>audioTrackIndex negative or past the last audio track (400).</summary>
        public const string InvalidAudioTrack = "invalid_audio_track";
        /// <summary>maxHeight negative or outside <see cref="MinMaxHeight"/>..<see cref="MaxMaxHeight"/> (400).</summary>
        public const string InvalidMaxHeight = "invalid_max_height";
        /// <summary>maxBitrate negative (400).</summary>
        public const string InvalidMaxBitrate = "invalid_max_bitrate";
        /// <summary>burnSubtitle negative or past the last subtitle track (400).</summary>
        public const string InvalidSubtitleTrack = "invalid_subtitle_track";
        /// <summary>The text subtitle to burn in couldn't be converted (422;
        /// the detail says why).</summary>
        public const string SubtitleUnavailable = "subtitle_unavailable";

        public string Code { get; } = code;
        public int StatusCode { get; } = statusCode;
    }

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
    ///
    /// <see cref="TranscodeReason"/> is English text for the Info popup;
    /// <see cref="TranscodeReasonCode"/> is the machine-readable main reason
    /// (<see cref="ReasonCodes"/>) and <see cref="TranscodeReasonCodes"/> every
    /// reason that applies, main one first — for a localized HUD.
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
        string? TranscodeReason,
        string? TranscodeReasonCode = null,
        string[]? TranscodeReasonCodes = null,
        // HLS sessions: "keyframe" (segments start on the file's key frames —
        // task 6.2 copy) or "grid" (4 s grid: re-encodes, the fallback copy).
        string? Segmentation = null);

    /// <summary>
    /// Machine-readable reasons for what the server did to a stream
    /// (<c>output.transcodeReasonCode</c> / <c>transcodeReasonCodes</c>). The
    /// first group explains a video re-encode, the second a remux / copy, the
    /// last are side notes.
    /// </summary>
    public static class ReasonCodes
    {
        /// <summary>The user picked a height below the source's (Quality menu).</summary>
        public const string UserQualityCap = "user_quality_cap";
        /// <summary>The user picked a bitrate below the source's (Bitrate menu).</summary>
        public const string UserBitrateCap = "user_bitrate_cap";
        /// <summary>The client can't decode the video (codec / profile / bit depth: HEVC, Hi10P, AV1, MPEG-4 …).</summary>
        public const string CodecUnsupported = "codec_unsupported";
        /// <summary>Dolby Vision profile 5 without a DV decoder — re-encoded (tone-mapped).</summary>
        public const string DolbyVision = "dolby_vision";
        /// <summary>Interlaced video for a browser — re-encoded to deinterlace it.</summary>
        public const string Interlaced = "interlaced";
        /// <summary>The client can't open the container — remuxed (Direct Stream / HLS copy).</summary>
        public const string ContainerUnsupported = "container_unsupported";
        /// <summary>The audio is transcoded: the client can't decode it, or the container can't carry it.</summary>
        public const string AudioUnsupported = "audio_unsupported";
        /// <summary>A non-default audio track was picked (the raw file would play its default one).</summary>
        public const string AudioTrack = "audio_track";
        /// <summary>An external dub is muxed in.</summary>
        public const string ExternalAudio = "external_audio";
        /// <summary>HDR was tone-mapped to SDR on the re-encode.</summary>
        public const string HdrTonemap = "hdr_tonemap";
        /// <summary>HDR was re-encoded to SDR without a tone mapper (washed-out colours).</summary>
        public const string HdrLost = "hdr_lost";
        /// <summary>A subtitle track is drawn into the picture (burnSubtitle) — needs a re-encode.</summary>
        public const string BurnedSubtitle = "burned_subtitle";
    }

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
    public async Task<PlaybackDecision> ChoosePlaybackAsync(string fullPath, ClientCaps client,
        CancellationToken ct = default,
        // The Quality / Bitrate menu's caps (0 = none). One that really shrinks
        // the stream — a height below the source's, a bitrate below the
        // source's — needs the HLS re-encode, so neither native path applies.
        // One at/above the source (a "1080p" pick on a 1080p file, "≤ 20 Mbps"
        // on a 6 Mbps one) changes nothing and keeps the native path.
        int maxHeight = 0, int maxBitrate = 0,
        // The audio track to play (0 = first). Direct Play serves the file as
        // it is — the player picks its own default track — so another track
        // rules it out; the Direct Stream remux maps exactly the one asked for
        // (/api/video ?audioTrackIndex=), so a track switch keeps the cheap
        // remux instead of falling to an HLS session (B19).
        int audioTrackIndex = 0)
    {
        var probe = await ProbeMediaAsync(fullPath, ct);
        var duration = probe?.DurationSec ?? 0;
        if (probe is null) return new PlaybackDecision(false, null, duration, null);
        if ((maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height)
            || EffectiveBitrateCap(maxBitrate, probe) > 0)
            return new PlaybackDecision(false, null, duration, null);
        // An index past the last track: no native path — StartAsync answers it
        // with the 400 invalid_audio_track.
        if (audioTrackIndex < 0 || (audioTrackIndex > 0 && audioTrackIndex >= probe.AudioStreams.Count))
            return new PlaybackDecision(false, null, duration, null);
        // Audio fields of the track the remux would map.
        var track = probe.ForAudioTrack(audioTrackIndex, null, false);

        var container = Path.GetExtension(fullPath).ToLowerInvariant().TrimStart('.');

        // ── Native client (Android TV ExoPlayer) — capability negotiation ──
        // The browser eligibility rules below are wrong for ExoPlayer: it
        // demuxes MKV/AVI/TS itself and the device usually decodes MPEG4-ASP,
        // AC3/E-AC3 etc. The client probes MediaCodecList and sends the token
        // list (containers + codecs); we serve the widest tier the DEVICE can
        // take instead of burning CPU on a transcode a browser would need.
        // Old AVI/XviD rips are the poster child: full re-encode for the web,
        // plain /api/file for the TV.
        // Since task 1.2 the web player reports the same tokens (MediaCapabilities),
        // so this branch serves browsers that sent them too.
        // Dolby Vision for this client (master plan §5.4, DolbyVision.For):
        // P8.x plays as its HEVC base layer, P7 needs its RPU stripped, P5
        // needs a DV decoder or a re-encode. Since the web player sends tokens
        // too, this branch is also where a browser's P8 MKV must keep Direct
        // Stream — the native <video> path (RTX VSR / RTX HDR need it) it had
        // before the browser reported caps.
        var dovi = DolbyVision.For(probe.Dovi, client);
        // Interlaced video plays combed in a browser (no deinterlacer on any
        // native path) — only a native player takes it as is; for a browser
        // the HLS re-encode deinterlaces it (ChoosePlan).
        bool interlacedForBrowser = probe.Interlaced && !client.NativePlayer;
        if (client.HasTokens)
        {
            bool videoOk = NativeVideoOk(probe, client) && dovi != DoviHandling.Reencode && !interlacedForBrowser;
            bool audioOk = probe.AudioStreams.Count == 0 || client.DecodesAudio(probe.AudioCodec);
            bool containerOk = client.Has(container);
            // Direct Play hands over the raw file, DV signalling and all: fine
            // for a DV decoder, and for P8 where the player falls back to the
            // base layer itself — ExoPlayer does (dvhe.08 → HEVC); a browser
            // only when the MP4 sample entry is plain hvc1/hev1 (a dvh1 / dvhe
            // entry fails its codec check). Never for P7 (no fallback anywhere:
            // it needs the strip, i.e. the remux).
            bool rawOk = dovi switch
            {
                DoviHandling.None or DoviHandling.AsDolbyVision => true,
                DoviHandling.AsBaseLayer => client.NativePlayer || !IsDolbyVisionSampleEntry(probe.VideoCodecTag),
                _ => false,
            };

            if (audioTrackIndex == 0 && videoOk && rawOk && audioOk && containerOk)
            {
                var nativeUrl = "/api/file?path=" + Uri.EscapeDataString(fullPath);
                var nativeOut = BuildOutputInfo(probe, plan: null, isDirectPlay: true, dovi: dovi);
                return new PlaybackDecision(true, nativeUrl, duration, nativeOut);
            }
            if (videoOk && client.Has("mp4"))
            {
                // Device decodes the video but not the audio (or can't demux the
                // container, or wants another track) → /api/video: video
                // stream-copy, audio copied when the device decodes it (else
                // transcoded, AudioPolicy). Still ~0% CPU next to a re-encode.
                var why = !containerOk ? ReasonCodes.ContainerUnsupported
                    : !audioOk ? ReasonCodes.AudioUnsupported
                    : audioTrackIndex > 0 ? ReasonCodes.AudioTrack
                    : ReasonCodes.DolbyVision;   // a DV stream the raw file can't carry (P7 strip, dvh1 entry)
                // remux=hls: the same need, met by the key-frame HLS copy (StartAsync).
                if (client.HlsRemux) return new PlaybackDecision(false, null, duration, null);
                return DirectStreamDecision(fullPath, track, client, audioTrackIndex, duration, why);
            }
            // Video codec beyond the device → HLS transcode as usual.
            return new PlaybackDecision(false, null, duration, null);
        }

        bool clientHevc = client.Hevc, clientHevc10 = client.Hevc10;
        if (audioTrackIndex == 0 && dovi == DoviHandling.None && !interlacedForBrowser
            && IsDirectPlayEligible(container, probe, clientHevc, clientHevc10))
        {
            // /api/file serves the raw bytes with Range support — exactly what
            // <video> needs for native seek. URL-escape the path so spaces and
            // unicode survive (file paths frequently have both).
            var directUrl = "/api/file?path=" + Uri.EscapeDataString(fullPath);
            var output = BuildOutputInfo(probe, plan: null, isDirectPlay: true);
            return new PlaybackDecision(true, directUrl, duration, output);
        }

        if (dovi != DoviHandling.Reencode && !interlacedForBrowser
            && IsDirectStreamEligible(container, probe, clientHevc, clientHevc10))
        {
            // /api/video remuxes the source to progressive fMP4 (video copy,
            // audio→AAC). The browser plays it as a native <video>, which is
            // why we route MKV here instead of HLS: original video bitstream +
            // HDR are preserved, and native playback outputs HDR more reliably
            // than MSE. The client owns the seek-reload dance (?seek=N).
            if (client.HlsRemux) return new PlaybackDecision(false, null, duration, null);
            return DirectStreamDecision(fullPath, track, client, audioTrackIndex, duration,
                ReasonCodes.ContainerUnsupported);
        }

        return new PlaybackDecision(false, null, duration, null);
    }

    /// <summary>
    /// The Direct Stream answer: the /api/video URL — carrying the track and
    /// the client's caps, so the remux (and every <c>&amp;seek=N</c> reload the
    /// client appends) maps that track and makes the same audio decision for
    /// the same client — and the Info block for what it will deliver.
    /// </summary>
    private static PlaybackDecision DirectStreamDecision(string fullPath, ProbeInfo track, ClientCaps client,
        int audioTrackIndex, double duration, string reasonCode)
    {
        var url = "/api/video?path=" + Uri.EscapeDataString(fullPath)
            + (audioTrackIndex > 0 ? "&audioTrackIndex=" + audioTrackIndex.ToString(CultureInfo.InvariantCulture) : "")
            + client.ToQuery();
        var audio = AudioPolicy.Decide(track.AudioCodec, track.AudioChannels, track.AudioProfile, client,
            AudioOutput.Mp4Remux);
        var output = BuildOutputInfo(track, plan: null, isDirectPlay: false, isDirectStream: true, audio: audio,
            dovi: DolbyVision.For(track.Dovi, client), reasonCode: reasonCode,
            extraReasons: audioTrackIndex > 0 ? [ReasonCodes.AudioTrack] : null);
        return new PlaybackDecision(false, url, duration, output, DirectStream: true);
    }

    /// <summary>An MP4 sample entry that announces Dolby Vision (dvh1 / dvhe /
    /// dav1) rather than plain HEVC / AV1.</summary>
    private static bool IsDolbyVisionSampleEntry(string? tag)
        => (tag ?? "").ToLowerInvariant() is "dvh1" or "dvhe" or "dav1" or "dva1" or "dvav";

    /// <summary>Picks the HDR → SDR tone mapper for a re-encode of an HDR
    /// source (None for SDR sources and copy plans): libplacebo on the VAAPI
    /// plan when the startup probe found a real Vulkan GPU, the CPU tonemapx
    /// otherwise; tonemap_cuda on NVENC. <c>AppSettings:HdrToneMapping</c>
    /// can force "libplacebo" / "tonemapx" or turn it "off".</summary>
    private HdrToneMapper ToneMapperFor(HlsPlan plan, ProbeInfo? probe)
    {
        if (probe is null || !HdrToneMap.IsHdr(probe.ColorTransfer, probe.HasDolbyVision)) return HdrToneMapper.None;
        if (_toneMapMode == "off") return HdrToneMapper.None;
        var caps = _hardware?.Current.ToneMapping;
        bool tonemapx = caps?.Tonemapx == true;
        var cpu = tonemapx ? HdrToneMapper.Tonemapx : HdrToneMapper.None;
        if (_toneMapMode == "tonemapx") return plan is HlsPlan.Fmp4VaapiReencode or HlsPlan.Fmp4NvencReencode
            or HlsPlan.Fmp4SoftwareReencode ? cpu : HdrToneMapper.None;
        return plan switch
        {
            HlsPlan.Fmp4VaapiReencode    => caps?.Libplacebo == true ? HdrToneMapper.Libplacebo : cpu,
            HlsPlan.Fmp4NvencReencode    => _toneMapMode != "libplacebo" && caps?.TonemapCuda == true
                                                ? HdrToneMapper.TonemapCuda : cpu,
            HlsPlan.Fmp4SoftwareReencode => cpu,
            _                            => HdrToneMapper.None,
        };
    }

    /// <summary>
    /// Builds the <see cref="PlayerOutputInfo"/> the HUD reads off — the
    /// stream the player actually receives after our serving decision. For
    /// direct play and stream-copy paths it mirrors the source; for re-encode
    /// paths it reports the post-transcode codec/bit-depth/HDR state.
    /// </summary>
    private static PlayerOutputInfo BuildOutputInfo(ProbeInfo probe, HlsPlan? plan, bool isDirectPlay,
        bool isDirectStream = false, HdrToneMapper toneMapper = HdrToneMapper.None, AudioDecision? audio = null,
        DoviHandling dovi = DoviHandling.None, VideoTarget target = VideoTarget.H264,
        // Why: the main reason code (ReasonCodes; null for Direct Play) and the
        // details its text needs — the effective bitrate cap, the source codec.
        string? reasonCode = null, int capMbps = 0, IEnumerable<string>? extraReasons = null,
        string? segmentation = null)
    {
        // Re-encode paths flatten to H.264 8-bit SDR. Everything else preserves
        // the source bitstream (Direct Play, TS copy, fMP4 stream-copy).
        bool isReencode = plan is HlsPlan.Fmp4VaapiReencode
                               or HlsPlan.Fmp4NvencReencode
                               or HlsPlan.Fmp4SoftwareReencode;

        var hdrFormats = new List<string>();
        if (isReencode && target == VideoTarget.Hevc10Hdr)
        {
            // HDR kept through the re-encode (HEVC Main10, BT.2020 tags).
            hdrFormats.Add(string.Equals(probe.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase)
                ? "hlg" : "hdr10");
        }
        else if (!isReencode)
        {
            // Dolby Vision only where the player gets — and decodes — it; P8 as
            // its base layer and stripped P7 play as the HDR10 / HLG below.
            if (probe.HasDolbyVision && dovi == DoviHandling.AsDolbyVision) hdrFormats.Add("dolbyvision");
            var xfer = (probe.ColorTransfer ?? "").ToLowerInvariant();
            if (xfer == "smpte2084") hdrFormats.Add("hdr10");
            else if (xfer == "arib-std-b67") hdrFormats.Add("hlg");
        }
        var hdr = hdrFormats.Count > 0 ? hdrFormats[0] : "sdr";

        bool hevcOut = isReencode && target != VideoTarget.H264;
        string videoCodec = isReencode ? (hevcOut ? "hevc" : "h264") : (probe.VideoCodec ?? "");
        int    bitDepth   = isReencode ? (hevcOut ? 10 : 8) : (probe.Is10Bit ? 10 : 8);

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

        // What happened to an HDR source on a re-encode: tone-mapped to SDR
        // (correct colours) or — tone mapping off / unavailable — just lost.
        var hdrNote = !isReencode || !HdrToneMap.IsHdr(probe.ColorTransfer, probe.HasDolbyVision) ? ""
            : target == VideoTarget.Hevc10Hdr ? " (HDR kept)"
            : toneMapper != HdrToneMapper.None ? $" (HDR → SDR tone-mapped, {HdrToneMap.Describe(toneMapper)})"
            : " (HDR lost)";
        var outName = hevcOut ? "HEVC Main10" : "H.264";
        // Why it was re-encoded, in words — the text used to say "for browser
        // compatibility" even when the user had picked a lower quality.
        var why = reasonCode switch
        {
            ReasonCodes.UserQualityCap => $"quality limited to {probe.Height}p",
            ReasonCodes.UserBitrateCap => $"bitrate limited to {capMbps} Mbps",
            ReasonCodes.Interlaced     => "deinterlaced for the browser",
            ReasonCodes.DolbyVision    => "Dolby Vision profile 5 needs a DV decoder",
            ReasonCodes.BurnedSubtitle => "subtitles burned in",
            _                          => $"the client can't decode {DescribeVideo(probe)}",
        };
        string? reason = isDirectPlay ? null : isDirectStream
            ? "Remuxed to MP4 — original video bitstream, no re-encode"
            : plan switch
        {
            HlsPlan.TsStreamCopy      => "H.264 source remuxed to MPEG-TS for HLS delivery",
            HlsPlan.Fmp4VaapiReencode => $"Re-encoded to {outName} via VAAPI (GPU): {why}" + hdrNote,
            HlsPlan.Fmp4NvencReencode => $"Re-encoded to {outName} via NVENC (GPU): {why}" + hdrNote,
            HlsPlan.Fmp4SoftwareReencode => $"Re-encoded to H.264 via CPU (libx264): {why}" + hdrNote,
            HlsPlan.Fmp4StreamCopy    => "HEVC stream-copied to fMP4 — original bitstream, no re-encode (browser decodes HEVC; HDR/10-bit preserved)",
            _                          => null,
        };

        // Every reason that applies, main one first: the HDR outcome of a
        // re-encode, a transcoded audio track, the caller's extras (track, dub).
        var codes = new List<string>();
        if (!isDirectPlay)
        {
            if (reasonCode is not null) codes.Add(reasonCode);
            if (isReencode && target != VideoTarget.Hevc10Hdr && HdrToneMap.IsHdr(probe.ColorTransfer, probe.HasDolbyVision))
                codes.Add(toneMapper != HdrToneMapper.None ? ReasonCodes.HdrTonemap : ReasonCodes.HdrLost);
            if (audio is { Copy: false } && reasonCode != ReasonCodes.ExternalAudio
                && !(extraReasons?.Contains(ReasonCodes.ExternalAudio) ?? false))
                codes.Add(ReasonCodes.AudioUnsupported);
            foreach (var extra in extraReasons ?? []) if (!codes.Contains(extra)) codes.Add(extra);
        }

        // Audio output reflects what the player actually receives, not the
        // source: the file's own track on Direct Play, else the stream's
        // AudioDecision (a copy keeps codec + channels; a transcode reports
        // e.g. AAC 2.0 or E-AC-3 5.1, not the source's TrueHD 7.1). probe's
        // audio fields are the SELECTED track's (ProbeInfo.ForAudioTrack).
        var audioCodecOut = isDirectPlay ? probe.AudioCodec ?? "" : audio?.Codec ?? "aac";
        var audioChannelsOut = isDirectPlay ? probe.AudioChannels : audio?.Channels ?? 2;

        return new PlayerOutputInfo(
            Plan:            planName,
            Container:       container,
            VideoCodec:      videoCodec,
            BitDepth:        bitDepth,
            Hdr:             hdr,
            HdrFormats:      hdrFormats.ToArray(),
            Width:           probe.Width,
            Height:          probe.Height,
            AudioCodec:      audioCodecOut,
            AudioChannels:   audioChannelsOut,
            AudioLanguage:   probe.AudioLanguage ?? "",
            Transcoded:      !isDirectPlay && !isDirectStream,
            TranscodeReason: reason,
            TranscodeReasonCode: codes.Count > 0 ? codes[0] : null,
            TranscodeReasonCodes: codes.ToArray(),
            Segmentation: segmentation);
    }

    /// <summary>"HEVC 10-bit", "H.264 10-bit", "MPEG-4" … for the reason text.</summary>
    private static string DescribeVideo(ProbeInfo probe)
    {
        var name = (probe.VideoCodec ?? "").ToLowerInvariant() switch
        {
            "h264"  => "H.264",
            "hevc"  => "HEVC",
            "av1"   => "AV1",
            "vp9"   => "VP9",
            "vp8"   => "VP8",
            "mpeg4" => "MPEG-4",
            "mpeg2video" or "mpeg2" => "MPEG-2",
            "vc1"   => "VC-1",
            "" => "this video",
            var other => other.ToUpperInvariant(),
        };
        return probe.Is10Bit ? name + " 10-bit" : name;
    }

    /// <summary>Codec eligibility against the client's capability tokens.
    /// 10-bit HEVC needs the explicit <c>hevc10</c> token. 10-bit H.264
    /// (High10) has no hardware decoders on TV SoCs → transcode.</summary>
    private static bool NativeVideoOk(ProbeInfo probe, ClientCaps caps)
    {
        // (Dolby Vision is judged by the caller — DolbyVision.For.)
        var vc = (probe.VideoCodec ?? "").ToLowerInvariant();
        return vc switch
        {
            "h264"  => !probe.Is10Bit && caps.Has("h264"),
            "hevc"  => caps.Has(probe.Is10Bit ? "hevc10" : "hevc"),
            "mpeg4" => caps.Has("mpeg4"),
            "mpeg2" or "mpeg2video" => caps.Has("mpeg2"),
            "vp9"   => caps.Has("vp9"),
            "vp8"   => caps.Has("vp8"),
            "av1"   => caps.Has("av1"),
            _       => false,
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
    /// audio copied or transcoded), so the only gate is the video codec /
    /// bit-depth vs the client's reported HEVC support, which makes this wider
    /// than Direct Play. Dolby Vision is allowed here (unlike Direct Play) where
    /// the remux can make it plain HEVC — P8 tagged hvc1 plays as HDR10, P7 gets
    /// its RPU stripped; P5 is ruled out by the caller (it needs a re-encode).
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
        // What the client decodes (ClientCaps: the nativeCaps tokens,
        // maxAudioChannels, the clientHevc MSE flag). HEVC lets ChoosePlan ship
        // HEVC as a stream-copy instead of transcoding it to H.264; the audio
        // tokens + channel count drive AudioPolicy. null = legacy browser.
        ClientCaps? client = null,
        // Cap output video bitrate in Mbps (player Bitrate menu). >0 forces a
        // re-encode at this bitrate; 0 = no cap. Carried on the session so
        // seek-restarts keep it (mirrors maxHeight).
        int maxBitrate = 0,
        // Whose session this is (account + client device, see SessionOwner).
        // Dedup and cap eviction only ever replace the caller's own sessions
        // or idle ones — never another client's live playback.
        SessionOwner? owner = null,
        // Embedded subtitle track N (0:s:N) to draw into the picture, -1 = none.
        // Forces a video re-encode (PrepareBurnAsync / ChoosePlan).
        int burnSubtitle = -1)
    {
        var who = owner ?? SessionOwner.Unknown;
        var caps = client ?? ClientCaps.Legacy;
        bool clientHevc = caps.Hevc;
        // The endpoint already validated these (ValidateStartOptions); keep the
        // encoder-safe invariants for any other caller: even cap, no negatives.
        maxHeight = Math.Max(0, maxHeight) & ~1;
        maxBitrate = Math.Max(0, maxBitrate);
        var requestedBitrate = maxBitrate;
        if (audioTrackIndex < 0)
            throw new StartFailedException(StartFailedException.InvalidAudioTrack, StatusCodes.Status400BadRequest,
                $"audioTrackIndex must be 0 or greater (got {audioTrackIndex}).");

        // 1. Probe so we know the duration + codec hints upfront. Without
        //    duration we can't build a VOD playlist — fall back to live mode.
        //    Runs BEFORE the pre-flight cleanup so a request rejected below (or
        //    abandoned meanwhile — the probe throws then) can't have torn down
        //    this client's current session first. Cached per file version
        //    (MediaProbeCache), so it's cheap on a restart.
        var probe = await ProbeMediaAsync(fullPath, ct);
        var hasExternalAudio = externalAudioPath is not null;
        var externalProbe = hasExternalAudio ? await ProbeMediaAsync(externalAudioPath!, ct) : null;
        // An audio index past the last track used to map nothing (the `?` in
        // -map 0:a:N?) — a silent stream with no error. Reject it instead.
        // Unknown when the probe failed (then ffmpeg gets its chance as before).
        var audioTracks = hasExternalAudio ? externalProbe?.AudioStreams.Count : probe?.AudioStreams.Count;
        if (audioTracks is int count && audioTrackIndex > 0 && audioTrackIndex >= count)
            throw new StartFailedException(StartFailedException.InvalidAudioTrack, StatusCodes.Status400BadRequest,
                $"audioTrackIndex {audioTrackIndex} is out of range: the {(hasExternalAudio ? "external audio file" : "source")} has {count} audio track{(count == 1 ? "" : "s")}.");
        // From here on the probe's audio fields describe the track the session
        // MAPS (source track N, or the external dub's) instead of track 0, so
        // TS passthrough, CODECS, the Info block and the restart agree with it.
        probe = probe?.ForAudioTrack(audioTrackIndex, externalProbe, hasExternalAudio);
        // Burn-in: checked and prepared (ASS conversion, fonts) before anything
        // is torn down, so a bad track number is a plain 400.
        var burn = burnSubtitle >= 0 || burnSubtitle < -1
            ? await PrepareBurnAsync(fullPath, probe, burnSubtitle, ct)
            : null;
        // A bitrate cap at/above the source's own bitrate can't shrink anything
        // — it must not turn a copy into a transcode (EffectiveBitrateCap).
        maxBitrate = EffectiveBitrateCap(maxBitrate, probe);
        if (maxBitrate != requestedBitrate)
            _logger.LogInformation("HLS: ignoring the {Cap} Mbps cap for {Path} — the source is only {Src:F1} Mbps",
                requestedBitrate, fullPath, (probe?.BitRate ?? 0) / 1e6);

        // ─── Pre-flight: what this start replaces, then the cap ─────────────
        // 1) This CLIENT's existing sessions for the same source file — a
        //    second Play on the same movie from the same player (another audio
        //    track / quality, a reload) means it wants the new one. The key is
        //    (user, device, path), not the path alone: the path-only dedup tore
        //    down the TV's stream when a browser opened the same episode, or one
        //    profile's when another started it.
        //    They are stopped only once THIS session has produced its first
        //    segment (end of the startup wait below), not up front: stopping
        //    them first meant a start that then failed (ffmpeg error, a 4xx/5xx)
        //    left the player with no stream at all, while the old one could
        //    have kept playing as the fallback. The same rule covers a start
        //    that is still in its own startup wait — it is superseded only by a
        //    newer start that actually works (and then answers 409 superseded).
        var replacing = _sessions
            .Where(kv => kv.Value.Owner == who
                      && string.Equals(kv.Value.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        // 2) Enforce the global cap. The sessions being replaced don't count —
        //    they are on their way out, and must survive as the fallback until
        //    then — so while both exist the host briefly runs up to
        //    MaxConcurrentSessions + replacing.Count. Otherwise evict, in this
        //    order, the caller's own oldest session (leaked tabs on other
        //    files), then the least recently used IDLE session of anyone (no
        //    segment fetch / keepalive for ActiveSessionGrace: a closed tab or
        //    a crashed client). Other clients' live sessions are never evicted:
        //    when every slot is one, this start fails with 503 instead of
        //    cutting off someone's playback.
        while (_sessions.Count - _sessions.Keys.Count(replacing.Contains) >= MaxConcurrentSessions)
        {
            var idleCutoff = DateTime.UtcNow - ActiveSessionGrace;
            var victim = _sessions
                .Where(kv => kv.Value.Owner == who && !replacing.Contains(kv.Key))
                .OrderBy(kv => kv.Value.LastActive)
                .Select(kv => kv.Key)
                .FirstOrDefault()
                ?? _sessions
                .Where(kv => kv.Value.LastActive < idleCutoff && !replacing.Contains(kv.Key))
                .OrderBy(kv => kv.Value.LastActive)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (victim is null)
            {
                _logger.LogWarning("HLS: at the session cap ({Max}) and every session is another client's live playback — refusing start for {Path} ({Owner})",
                    MaxConcurrentSessions, fullPath, who);
                throw new StartFailedException(StartFailedException.TooManySessions, StatusCodes.Status503ServiceUnavailable,
                    $"The server is already streaming {MaxConcurrentSessions} sessions to other players.");
            }
            _logger.LogInformation("HLS: at session cap, evicting {Token} ({Owner})", victim,
                _sessions.TryGetValue(victim, out var v) ? v.Owner : default);
            Stop(victim);
        }

        var token = Guid.NewGuid().ToString("N");
        var dir   = Path.Combine(_rootDir, token);
        Directory.CreateDirectory(dir);
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
        var plan = probe is not null ? ChoosePlan(probe, caps, maxHeight, maxBitrate, burning: burn is not null)
            : HlsPlan.Fmp4StreamCopy;
        // Dolby Vision on the fMP4 copy: dvh1 for a DV client, hvc1 base layer
        // for P8, RPU strip for P7 (ChoosePlan never copies P5 for a non-DV
        // client). Irrelevant to re-encodes — they decode the base layer.
        var dovi = probe is not null && plan == HlsPlan.Fmp4StreamCopy
            ? DolbyVision.For(probe.Dovi, caps) : DoviHandling.None;
        // What a re-encode writes: H.264 8-bit, or HEVC Main10 where the client
        // and this plan's encoder can (ChooseVideoTarget).
        var target = ChooseVideoTarget(plan, probe, caps, burning: burn is not null);
        // Why (output.transcodeReasonCode): the plan's reason, or for a copy the
        // dub that forced the HLS mux instead of a native path.
        bool copyPlan = plan is HlsPlan.TsStreamCopy or HlsPlan.Fmp4StreamCopy;
        var reasonCode = probe is null ? null
            : copyPlan && hasExternalAudio ? ReasonCodes.ExternalAudio
            // A copy for a client that opens this container: it's here for the
            // audio track (Direct Play plays the file's default one).
            : copyPlan && audioTrackIndex > 0
              && caps.Has(Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant()) ? ReasonCodes.AudioTrack
            : PlanReason(plan, probe, caps, maxHeight, maxBitrate, burning: burn is not null);
        // The audio half (AudioPolicy): copy the mapped track when the client
        // decodes it and the plan's container (TS / fMP4) carries it, else
        // transcode — 5.1 for a 5.1-capable client, else a stereo downmix. An
        // external dub is always transcoded (its probe may have failed — then
        // its codec and channels are unknown, so treat it as stereo).
        var audio = AudioPolicy.Decide(
            hasExternalAudio ? null : probe?.AudioCodec,
            hasExternalAudio && externalProbe is null ? 2 : probe?.AudioChannels ?? 2,
            probe?.AudioProfile, caps,
            plan == HlsPlan.TsStreamCopy ? AudioOutput.HlsTs : AudioOutput.HlsFmp4);
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
            var videoTag = target == VideoTarget.H264 ? "avc1.4d401f" : Hevc10CodecsTag(outH);
            probeForPlaylist = probe with
            {
                CodecsAttribute = $"{videoTag},{audio.CodecsTag}",
                Width  = outW,
                Height = outH,
            };
        }
        else if (probe is not null && plan == HlsPlan.TsStreamCopy)
        {
            // MPEG-TS branch: declare the source-native CODECS so browsers know
            // what they're getting; the audio part is what AudioPolicy decided
            // for the SELECTED track (copied codec, or the transcode target).
            probeForPlaylist = probe with { CodecsAttribute = $"avc1.640028,{audio.CodecsTag}" };
        }
        else if (probe is not null && plan == HlsPlan.Fmp4StreamCopy && probe.VideoCodecsAttribute is { } vAttr)
        {
            var videoTag = dovi == DoviHandling.AsDolbyVision ? DolbyVision.CodecsTag(probe.Dovi) : vAttr;
            probeForPlaylist = probe with { CodecsAttribute = $"{videoTag},{audio.CodecsTag}" };
        }
        // Segment layout + where the first ffmpeg run starts (B18). The player
        // places the first fragment it loads at that fragment's ANNOUNCED start,
        // so that fragment must really begin there:
        //   • re-encode: ffmpeg seeks to the segment boundary K*4 — not to the
        //     resume point — and decodes from exactly there; forced key frames
        //     then keep every later cut on the 4 s grid. Seeking to the resume
        //     point itself started seg-K up to 4 s late and shifted the whole
        //     timeline by that much.
        //   • stream copy: video can only start on a key frame, so the run
        //     starts at the last key frame before the resume point and the
        //     layout is anchored there (SegmentLayout.Anchored). Its announced
        //     end is where ffmpeg really cuts it — the next key frame after the
        //     resume point (no key frame lies between the two), or at least 4 s
        //     on — so the resume point sits inside it and the player asks for
        //     it first. Without this a copy resume on a sparse-GOP file started
        //     at a key frame 9 s early and the player's clock was off by that.
        // Earlier segments don't exist on disk; a backward scrub reaches them
        // via the WaitForFileAsync seek-restart. A resume point inside a folded
        // tail belongs to the last announced segment.
        bool isCopyPlan = plan is HlsPlan.TsStreamCopy or HlsPlan.Fmp4StreamCopy;
        var videoEnd = probe?.VideoEndSec ?? 0;
        var layout = SegmentLayout.Uniform(totalDuration, SegmentDurationSec, videoEnd);
        double ffmpegSeekSec = 0;
        int startSegment = 0;
        // Key-frame-exact copy (task 6.2, HlsSessionService.Keyframes.cs): the
        // playlist from the file's key frames, cut by the segment muxer at
        // exactly those times. Supersedes the anchored resume below.
        KeyframeIndex? keyframeIndex = null;
        if (isCopyPlan && probe is not null && totalDuration > 0 && _keyframes is not null
            && await KeyframeHlsEnabledAsync(ct))
            keyframeIndex = await _keyframes.GetAsync(fullPath, probe.VideoStreamIndex, KeyframeIndexBudget, ct);
        bool keyframeMode = keyframeIndex is not null;
        if (keyframeMode)
        {
            var timeline = keyframeIndex!.Times.Select(t => t - probe!.StartTimeSec).ToArray();
            layout = SegmentLayout.FromKeyframes(timeline, totalDuration, videoEnd, SegmentDurationSec);
            startSegment = seekSec > 0 ? layout.IndexOf(seekSec) : 0;
            var cut = layout.Cuts![startSegment];
            ffmpegSeekSec = startSegment > 0 ? CopySeekTarget(cut, null) : 0;
            _logger.LogDebug("HLS: key-frame layout for {Path}: {Kf} key frames ({Source}) → {Segs} segments, start seg-{Seg} at {Cut:F3}s",
                fullPath, keyframeIndex.Times.Length, keyframeIndex.Source, layout.Count, startSegment, cut);
        }
        else if (seekSec > 0 && layout.Count > 0)
        {
            var anchor = isCopyPlan && probe is not null
                ? await FindCopyAnchorAsync(fullPath, probe.VideoStreamIndex, seekSec, ct)
                : null;
            if (anchor is (double kf, var nextKf))
            {
                // Where ffmpeg will cut the first segment: the first key frame
                // at/after kf + 4 s — i.e. the next key frame after the resume
                // point when that is further, else ≥ kf + 4 (or, with no key
                // frame in the probe window, at least the window's end).
                var firstEnd = Math.Max(kf + SegmentDurationSec,
                    nextKf ?? seekSec + CopyAnchorWindowSec);
                layout = SegmentLayout.Anchored(totalDuration, SegmentDurationSec, kf, firstEnd, videoEnd);
                startSegment = layout.AnchorIndex;
                // Land the demuxer seek ON kf (see CopySeekTarget); 0 = from the
                // top, no seek at all. The first video packet copied IS that key
                // frame: with -copyts ffmpeg keeps the key frame the input seek
                // landed on (measured on E03: resume 19.94 s → seg-2 starts at
                // the 10.427 s key frame).
                ffmpegSeekSec = layout.Start(startSegment) > 0 ? CopySeekTarget(kf, nextKf) : 0;
                _logger.LogDebug("HLS: copy resume at {Seek:F3}s anchored on key frame {Kf:F3}s (next {Next}) → seg-{Seg} announced {Start:F3}..{End:F3}",
                    seekSec, kf, nextKf?.ToString("F3", CultureInfo.InvariantCulture) ?? "-",
                    startSegment, layout.Start(startSegment), layout.Start(startSegment) + layout.Duration(startSegment));
            }
            else
            {
                startSegment = layout.IndexOf(seekSec);
                ffmpegSeekSec = layout.Start(startSegment);
            }
        }
        var segCount = layout.Count;

        // 2. Write synthetic master + media playlists. If we couldn't probe
        //    duration, skip this and let ffmpeg's HLS muxer write them
        //    EVENT-style (the player will fall back to live UX).
        bool prewrittenPlaylists = false;
        if (segCount > 0 && probe is not null)
        {
            try
            {
                var bandwidth = plan is HlsPlan.TsStreamCopy or HlsPlan.Fmp4StreamCopy
                    ? probe.BitRate
                    : RateFor(targetHeight > 0 ? targetHeight : probe.Height, maxBitrate, probe).PeakKbps * 1000 + 192_000;
                File.WriteAllText(Path.Combine(dir, "master.m3u8"),
                    BuildMasterPlaylist(probeForPlaylist!, bandwidth), Encoding.ASCII);
                File.WriteAllText(Path.Combine(dir, "media.m3u8"),
                    BuildMediaPlaylist(layout, plan, startOffsetSec: seekSec), Encoding.ASCII);
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
        // runs ahead. Pre-load the measured delay as the baseline itsoffset; the
        // SW slider then trims on top. 0 for every other plan (re-encode strips
        // B-frames via -bf 0; TS carries PCR timing) — which is why the packet
        // probe runs only here instead of on every probe.
        var reorderDelaySec = plan == HlsPlan.Fmp4StreamCopy && probe is not null
            ? Math.Clamp(await MeasureReorderDelayAsync(fullPath, probe.VideoStreamIndex, ct) ?? probe.ReorderDelaySec, 0.0, 1.0)
            : 0.0;
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
        // An HDR-keeping HEVC Main10 output isn't tone-mapped at all.
        var toneMapper = target == VideoTarget.Hevc10Hdr ? HdrToneMapper.None : ToneMapperFor(plan, probe);
        _logger.LogInformation("HLS {Token}: plan={Plan} video={Target} audio={Audio} offset={OffsetMs}ms (codec={Vcodec}/{Acodec} {W}x{H} dv={Dv}/{DvHandling}) tonemap={ToneMap} client={Client} {Owner}",
            token, plan, target, audio, (int)Math.Round(audioOffsetSec * 1000),
            probe?.VideoCodec, probe?.AudioCodec, probe?.Width, probe?.Height, probe?.Dovi, dovi, toneMapper, caps, who);
        // ffmpeg starts at the segment start computed above (ffmpegSeekSec), not
        // at the resume point — the playlist's EXT-X-START still says seekSec.
        var (keyframeTimes, keyframeWindowEnd) = keyframeMode ? SegmentTimesFor(layout, startSegment) : ("", int.MaxValue);
        var args = BuildFfmpegArgs(fullPath, ffmpegSeekSec, dir, ffmpegMediaPath, ffmpegMasterName,
            probe, plan, startNumber: startSegment, reuseInit: false,
            targetHeight: targetHeight,
            audioOffsetSec: audioOffsetSec, audioTrackIndex: audioTrackIndex,
            externalAudioPath: externalAudioPath, maxBitrate: maxBitrate, toneMapper: toneMapper, audio: audio,
            dovi: dovi, target: target, burn: burn,
            keyframeRun: keyframeMode ? (keyframeTimes, KeyframeListPath(dir, 0)) : null);
        // Stderr drain (Warning-level log) + the tail an early exit is explained with.
        var stderrTail = new StderrTail();
        Process proc;
        Task stderrDrain;
        try { (proc, stderrDrain) = LaunchFfmpeg(args, dir, token, restartSegment: null, stderrTail); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ffmpeg launch failed for HLS session {Token} ({Path})", token, fullPath);
            TryRemoveDir(dir);
            throw;
        }

        var session = new HlsSession(token, fullPath, dir, proc, seekSec, layout,
            probe?.VideoCodec, plan, audioOffsetSec, totalDuration, audioTrackIndex, targetHeight,
            externalAudioPath, maxBitrate: maxBitrate, audioCodec: probe?.AudioCodec, is10Bit: probe?.Is10Bit ?? false,
            startSegment: startSegment, toneMapper: toneMapper, owner: who)
        {
            VideoStreamIndex = probe?.VideoStreamIndex ?? -1,
            Probe = probe,
            Audio = audio,
            Dovi = dovi,
            Target = target,
            Burn = burn,
            KeyframeMode = keyframeMode,
            KeyframeListPath = keyframeMode ? KeyframeListPath(dir, 0) : null,
            RunWindowEnd = keyframeWindowEnd,
            RunCount = 1,
        };
        _sessions[token] = session;
        // The caller gave up on this start (rapid Next/Prev, player closed
        // mid-start — the endpoint passes RequestAborted): stop the session's
        // ffmpeg right away instead of letting it encode on until the 5-min idle
        // GC. Scoped to the startup wait; disposed when this method returns.
        using var abandoned = ct.Register(() => Stop(token));

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
        //    (C) If ffmpeg dies before (A) is met, or (A) isn't met by the
        //        deadline, the start FAILS (StartFailedException → a 5xx with a
        //        reason) — returning a manifest whose first segment never comes
        //        just moved the hang into the player.
        var hardDeadline = DateTime.UtcNow + StartupTimeout;
        var segExt = SegmentExtension(plan);
        var initPath = Path.Combine(dir, "init.mp4");
        var firstSegPath = Path.Combine(dir, $"seg-{startSegment:D5}.{segExt}");
        var masterPath = Path.Combine(dir, "master.m3u8");
        bool needsInit = plan != HlsPlan.TsStreamCopy;
        // Phase A — wait for first segment.
        while (true)
        {
            FinalizeKeyframeSegments(session);
            var ready = (!needsInit || File.Exists(initPath))
                     && File.Exists(firstSegPath)
                     && File.Exists(masterPath);
            if (ready) break;
            if (!_sessions.ContainsKey(token))
            {
                // Stopped from outside while still starting: the caller gave up
                // (cancellation), or a newer start from the same client for the
                // same file got its first segment first and replaced this one.
                ct.ThrowIfCancellationRequested();
                throw new StartFailedException(StartFailedException.Superseded, StatusCodes.Status409Conflict,
                    "A newer start for the same file from this player replaced this one.");
            }
            if (proc.HasExited)
            {
                // Killed because the caller gave up (the RequestAborted → Stop
                // registration above) — that's a cancellation, not a failure.
                ct.ThrowIfCancellationRequested();
                var code = SafeExitCode(proc);
                // The reader may still be on ffmpeg's last lines — the ones that
                // say why. Give it a moment before reporting the tail.
                try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); } catch { }
                if (_sessions.TryRemove(token, out var dead))
                    try { dead.RestartLock.Dispose(); } catch { }
                TryRemoveDir(dir);
                _logger.LogWarning(
                    "HLS {Token}: ffmpeg exited (code {Code}) before producing the first segment — plan={Plan}, source={Path}. stderr tail:\n{Tail}",
                    token, code, plan, fullPath, stderrTail.Text is { Length: > 0 } t ? t : "(empty)");
                var why = stderrTail.LastLine;
                throw new StartFailedException(StartFailedException.FfmpegExited, StatusCodes.Status500InternalServerError,
                    $"ffmpeg exited (code {code}) before producing the first segment" + (why is null ? "." : $": {why}"));
            }
            if (DateTime.UtcNow >= hardDeadline)
            {
                _logger.LogWarning(
                    "HLS {Token}: no first segment after {Sec}s (init={Init}, seg-{Seg}={First}) — giving up; plan={Plan}, source={Path}. stderr tail:\n{Tail}",
                    token, (int)StartupTimeout.TotalSeconds, !needsInit || File.Exists(initPath), startSegment,
                    File.Exists(firstSegPath), plan, fullPath, stderrTail.Text is { Length: > 0 } t ? t : "(empty)");
                Stop(token);
                throw new StartFailedException(StartFailedException.FfmpegTimeout, StatusCodes.Status504GatewayTimeout,
                    $"ffmpeg produced no playable segment within {(int)StartupTimeout.TotalSeconds} s.");
            }
            await Task.Delay(150, ct);
        }

        // The new stream is playable — only now retire what it replaces (see
        // the pre-flight step): until here they were the fallback.
        foreach (var old in replacing)
        {
            if (!_sessions.ContainsKey(old)) continue;
            _logger.LogInformation("HLS: {Token} replaces prior session {Old} for the same source ({Owner})",
                token, old, who);
            Stop(old);
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
            FinalizeKeyframeSegments(session);
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
            ? BuildOutputInfo(probeForPlaylist ?? probe, plan, isDirectPlay: false, toneMapper: toneMapper,
                segmentation: keyframeMode ? "keyframe" : "grid",
                audio: audio, dovi: dovi, target: target, reasonCode: reasonCode, capMbps: maxBitrate,
                extraReasons: [.. hasExternalAudio ? new[] { ReasonCodes.ExternalAudio } : [],
                               .. burn is not null ? new[] { ReasonCodes.BurnedSubtitle } : []])
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

        // Every segment request moves the play frontier the pruning and the
        // look-ahead throttle key off — and un-pauses ffmpeg when the player is
        // closing in on (or asking past) what's been produced.
        var targetSeg = ParseSegmentNumber(fileName);
        if (targetSeg >= 0) NotePlayerRequest(session, targetSeg);

        FinalizeKeyframeSegments(session);
        if (File.Exists(full)) return full;

        // If the request is for a segment (seg-NNNNN.{m4s|ts}) and it's not
        // on disk yet, decide whether to wait or to skip ffmpeg ahead.
        if (targetSeg >= 0)
        {
            // Current run only (see HlsSession.ProducedTop): RunStartSegment-1
            // until ffmpeg flushes its first segment.
            var producedTop = CurrentProducedTop(session);
            var gap = targetSeg - producedTop;
            // Three cases:
            //   • forward, small gap (1..threshold): ffmpeg will reach it
            //     shortly, cheaper to wait than to restart. This also covers
            //     "nothing produced yet, target = the run's first segment" —
            //     gap 1, wait, don't thrash.
            //   • forward, big gap (>threshold): restart at target so we
            //     don't make the user wait minutes for sequential encode.
            //   • backward/at-or-below (target <= producedTop) and the file
            //     still isn't there: the user scrubbed back past the pruned
            //     window, or before this run's start (the resume point's
            //     start_number, an earlier restart). Restart at target to
            //     produce it.
            bool needsRestart = gap > RestartThresholdSegments || targetSeg <= producedTop
                // Key-frame runs: past the run's -segment_times window, or the
                // run finished (EOF / window end) without writing it.
                || (session.KeyframeMode && targetSeg < session.SegmentCount
                    && (targetSeg >= session.RunWindowEnd
                        || (session.Process.HasExited && SafeExitCode(session.Process) == 0)));
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
            FinalizeKeyframeSegments(session);
            if (File.Exists(full)) return full;
            if (session.Process.HasExited && session.Process.ExitCode != 0) return null;
            if (session.Process.HasExited) return File.Exists(full) ? full : null;
            await Task.Delay(150, ct);
        }
        return null;
    }

    /// <summary>
    /// <c>-ss</c> for a stream copy that must start ON the key frame at
    /// <paramref name="keyframeSec"/>. ffmpeg's input seek picks the key frame
    /// at or before its target, but for a container without exact pts seeking
    /// (Matroska, MPEG-TS) whose video has B-frames it first moves the target
    /// 3/23 s (≈ 0.13 s) EARLIER (the "dts heuristic" in fftools'
    /// ffmpeg_demux.c) — so <c>-ss 40.001</c> on a file with a key frame at
    /// exactly 40.0 started the copy at the 36.0 one (measured on E02), a whole
    /// GOP before the segment the playlist announced. Aim past that, while
    /// staying short of the next key frame (<paramref name="next"/>, when
    /// known). The 3-decimal argv rounding is covered by the same margin.
    /// </summary>
    private static double CopySeekTarget(double keyframeSec, double? next)
    {
        const double past = 3.0 / 23 + 0.01;
        return next is double n && n - keyframeSec <= past + 0.002
            ? keyframeSec + (n - keyframeSec) / 2
            : keyframeSec + past;
    }

    /// <summary>
    /// Checks and prepares a subtitle burn-in of embedded track
    /// <paramref name="index"/> (0:s:N). Bitmap tracks need nothing up front
    /// (the overlay reads the source's own stream); a text / ASS track is
    /// converted to ASS through SubtitleCache (cached — usually the player's
    /// own /api/subtitle?format=ass fetch made it already) and gets the file's
    /// attached fonts as libass's fontsdir.
    /// </summary>
    private async Task<SubtitleBurn> PrepareBurnAsync(string fullPath, ProbeInfo? probe, int index, CancellationToken ct)
    {
        var count = probe?.SubtitleCodecs.Count ?? 0;
        if (index < 0 || index >= count)
            throw new StartFailedException(StartFailedException.InvalidSubtitleTrack, StatusCodes.Status400BadRequest,
                $"burnSubtitle {index} is out of range: the source has {count} subtitle track{(count == 1 ? "" : "s")}.");
        var codec = probe!.SubtitleCodecs[index];
        if (Animarr.Web.Services.SubtitleCodecs.IsImage(codec)) return new SubtitleBurn(index, Image: true, null, null);
        if (_cachePaths is null)
            throw new StartFailedException(StartFailedException.SubtitleUnavailable,
                StatusCodes.Status422UnprocessableEntity, "Subtitle burn-in isn't available on this server.");

        var root = _cachePaths.CacheRoot;
        var ass = SubtitleCache.TryGetCached(fullPath, index, "ass", root);
        if (ass is null)
        {
            var r = await SubtitleCache.GetOrExtractAsync(fullPath, index, "ass", root, ct);
            if (r.Path is null)
                throw new StartFailedException(StartFailedException.SubtitleUnavailable,
                    StatusCodes.Status422UnprocessableEntity,
                    $"Subtitle track {index} can't be burned in ({r.FailureCode ?? SubtitleCache.ConvertFailed}).");
            ass = r.Path;
        }
        string? fontsDir = null;
        try { if (_fonts is not null) fontsDir = await _fonts.FontDirectoryAsync(fullPath, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "HLS burn-in: no attached fonts for {Path}", fullPath); }
        return new SubtitleBurn(index, Image: false, ass, fontsDir);
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
            // …and the previous runs' segments from the target onward: the new
            // run re-creates them in order, and leftovers ahead of it would be
            // served as if they were its output.
            DeleteSegmentsFrom(session.OutputDir, targetSegment);

            // The playlist spans the full file duration, so ffmpeg seeks to the
            // ANNOUNCED start of seg-K in the source (SegmentLayout — K*4 on
            // the uniform grid, the key frame itself for an anchored copy
            // segment), never session.SeekSec + K*4. A copy plan aims the
            // demuxer seek just past that start (CopySeekTarget) so a key frame
            // ON the boundary is the one picked, not the one a GOP before it.
            var segStart = session.KeyframeMode ? session.Layout.Cuts![targetSegment] : session.Layout.Start(targetSegment);
            var sourceSeekSec = (session.KeyframeMode ? targetSegment > 0 : segStart > 0)
                && session.Plan is HlsPlan.TsStreamCopy or HlsPlan.Fmp4StreamCopy
                ? CopySeekTarget(segStart, next: null)
                : segStart;
            // Key-frame run: the cuts from this segment's key frame on, a new list.
            var (kfTimes, kfWindowEnd) = session.KeyframeMode
                ? SegmentTimesFor(session.Layout, targetSegment) : ("", int.MaxValue);
            var kfList = session.KeyframeMode ? KeyframeListPath(session.OutputDir, session.RunCount) : null;
            // The restart runs off the START's probe (the plan is fixed for the
            // session's lifetime): same video map, codec, bit depth, height and
            // bitrate, and the MAPPED audio track's codec — with that missing the
            // TS passthrough decision flipped to re-encode after the first scrub.
            // The minimal stand-in is only for a session started without one.
            var restartProbe = session.Probe
                ?? new ProbeInfo(0, session.VideoCodec, session.AudioCodec, 0, 0, null, false, session.Is10Bit, null, 0, null)
                {
                    VideoStreamIndex = session.VideoStreamIndex,
                };
            var args = BuildFfmpegArgs(session.SourcePath, sourceSeekSec, session.OutputDir,
                Path.Combine(session.OutputDir, "_ffmpeg.m3u8"),
                "_ffmpeg_master.m3u8",
                restartProbe,
                session.Plan,
                startNumber: targetSegment,
                reuseInit: true,
                targetHeight: session.MaxHeight,
                audioOffsetSec: session.AudioOffsetSec,
                audioTrackIndex: session.AudioTrackIndex,
                externalAudioPath: session.ExternalAudioPath,
                maxBitrate: session.MaxBitrate,
                toneMapper: session.ToneMapper,
                audio: session.Audio,
                dovi: session.Dovi,
                target: session.Target,
                burn: session.Burn,
                keyframeRun: session.KeyframeMode ? (kfTimes, kfList!) : null);

            // Same launcher as StartAsync (stderr drained + logged per line).
            Process proc;
            try { (proc, _) = LaunchFfmpeg(args, session.OutputDir, session.Token, targetSegment, tail: null); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HLS: ffmpeg restart failed for {Token}", session.Token);
                return;
            }

            if (session.KeyframeMode)
            {
                lock (session.FinalizeLock)
                {
                    session.KeyframeListPath = kfList;
                    session.KeyframeListRead = 0;
                    session.RunWindowEnd = kfWindowEnd;
                    session.RunCount++;
                }
            }
            session.SwapProcess(proc, targetSegment);

            // Tell the playlist that segments from `targetSegment` onward come
            // from a fresh encoder context — without this marker hls.js tries
            // to continue the previous decoder pipeline through the gap and
            // produces the ~300ms audio-ahead glitch after every scrub.
            session.AddRestartBoundary(targetSegment);
            RegenerateMediaPlaylist(session);
        }
        finally
        {
            session.RestartLock.Release();
        }
    }

    public void Stop(string token)
    {
        if (!_sessions.TryRemove(token, out var s)) return;
        var proc = s.Process;
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
        try { s.RestartLock.Dispose(); } catch { }
        _logger.LogInformation("HLS session {Token} stopped (by client / GC).", token);

        // The recursive delete runs off the caller's path: Stop is called from
        // StartAsync's dedupe/cap step (i.e. BEFORE the next session can start),
        // from DELETE /api/hls and from the RequestAborted callback. Wait for the
        // killed ffmpeg first so it can't drop one more file into a dir being
        // deleted; anything that still fails is picked up by the GC's orphan
        // sweep (the token is no longer active).
        _ = Task.Run(async () =>
        {
            try
            {
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await proc.WaitForExitAsync(exited.Token);
            }
            catch { /* gone already, or didn't die in time — delete anyway */ }
            try { proc.Dispose(); } catch { }
            try { if (Directory.Exists(s.OutputDir)) Directory.Delete(s.OutputDir, recursive: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HLS: removing session dir {Dir} failed — the GC sweep will retry", s.OutputDir);
            }
        });
    }

}
