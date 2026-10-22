namespace Animarr.App.Services;

/// <summary>
/// Probes what the DEVICE can actually decode and turns it into the
/// comma-separated token list <c>/api/hls/start?nativeCaps=</c> expects.
/// The server uses it to pick the widest playback tier the device can take —
/// e.g. an AVI/XviD/AC3 rip Direct Plays on a TV whose SoC decodes MPEG4-ASP,
/// instead of burning the server CPU on the transcode a browser would need.
///
/// Containers are static per Media3 version (the bundled extractors demux
/// MP4/MKV/WebM/TS/AVI/FLV/Ogg/WAV regardless of device); codecs come from
/// <c>MediaCodecList</c> at runtime, so a phone without an AC3 license simply
/// doesn't advertise "ac3" and the server remuxes audio for it.
///
/// Honest about what a TV SoC really plays (B28):
/// <list type="bullet">
///   <item>HEVC / AV1 / VP9 count only with a HARDWARE decoder — the software
///         ones (c2.android.*) list 4K but can't keep up on a TV-class CPU —
///         and add a <c>&lt;codec&gt;@WxH</c> token: the largest picture a
///         hardware decoder takes (<c>hevc@1920x1088</c> on a 1080p projector
///         SoC). The server keeps tokens it doesn't know (≤ 16 chars).</item>
///   <item>MPEG-4 Part 2 only with a hardware decoder that has Advanced Simple
///         Profile (XviD / DivX): the software one is Simple Profile up to
///         352×288.</item>
///   <item>No <c>m2ts</c>: Media3's TsExtractor reads 188-byte packets only,
///         not Blu-ray's 192-byte BDAV packets.</item>
/// </list>
/// <see cref="CanDecodeSize"/> applies the same decoder rules to a concrete
/// picture size, and <see cref="HevcParams"/> derives the clientHevc flags
/// from them instead of always claiming HEVC Main10.
///
/// The display and audio output can change between plays (another TV on the
/// HDMI), so they are read on every <see cref="Get"/>: <c>hdr</c> when the
/// display shows HDR10 / HLG, <c>dv</c> when a Dolby Vision decoder AND a DV
/// display exist, and <see cref="MaxAudioChannels"/> from the HDMI sink.
/// </summary>
internal static class TvCodecCaps
{
    private static string? _cached;

    public static string Get()
    {
        var codecs = CodecTokens();
#if ANDROID
        var display = DisplayTokens();
        var all = display.Length == 0 ? codecs : codecs + "," + display;
        if (all != _lastLogged)
        {
            _lastLogged = all;
            global::Android.Util.Log.Info("Animarr.Caps", $"nativeCaps: {all} maxAudioChannels={MaxAudioChannels()}");
        }
        return all;
#else
        return codecs;
#endif
    }

#if ANDROID
    private static string? _lastLogged;
#endif

    /// <summary>Containers + codecs (MediaCodecList / FFmpeg — fixed per device).</summary>
    private static string CodecTokens()
    {
        if (_cached is not null) return _cached;
#if ANDROID
        var tokens = new List<string>
        {
            // Media3 bundled extractors (device-independent).
            "mp4", "m4v", "mov", "mkv", "webm", "ts", "avi", "flv", "ogg", "wav",
        };

        // mime → server token. Decoder presence probed per-device below.
        (string Mime, string Token)[] video =
        {
            ("video/avc",           "h264"),
            ("video/hevc",          "hevc"),
            ("video/mp4v-es",       "mpeg4"),
            ("video/mpeg2",         "mpeg2"),
            ("video/x-vnd.on2.vp8", "vp8"),
            ("video/x-vnd.on2.vp9", "vp9"),
            ("video/av01",          "av1"),
        };
        (string Mime, string Token)[] audio =
        {
            ("audio/mp4a-latm",     "aac"),
            ("audio/mpeg",          "mp3"),
            ("audio/ac3",           "ac3"),
            ("audio/eac3",          "eac3"),
            ("audio/vnd.dts",       "dts"),
            ("audio/true-hd",       "truehd"),
            ("audio/opus",          "opus"),
            ("audio/vorbis",        "vorbis"),
            ("audio/flac",          "flac"),
            ("audio/raw",           "pcm"),
        };

        try
        {
            foreach (var (mime, token) in video)
            {
                var usable = Usable(mime);
                LogDecoders(mime, usable);
                if (usable.Count == 0) continue;
                tokens.Add(token);
                if (HwOnly.Contains(mime) && MaxSize(usable) is { } max) tokens.Add($"{token}@{max}");
            }
            // HEVC Main10 → the separate "hevc10" token (10-bit HDR gate).
            if (Usable("video/hevc").Any(d => Has10BitProfile("video/hevc", d.Caps))) tokens.Add("hevc10");

            var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
            var infos = list.GetCodecInfos();
            foreach (var (mime, token) in audio)
                if (infos.Any(i => !i.IsEncoder && SupportsType(i, mime))) tokens.Add(token);
        }
        catch
        {
            // Probe failed — advertise the safe minimum every Android device has.
            tokens.AddRange(new[] { "h264", "aac", "mp3" });
        }

        // Audio the bundled FFmpeg extension decodes in software (Jellyfin's
        // media3-ffmpeg-decoder): the player falls back to it when the SoC has
        // no MediaCodec decoder, so these files can Direct Play as-is instead
        // of a server-side AAC 2.0 remux.
        foreach (var (mime, token) in audio)
            if (FfmpegSupports(mime))
                tokens.Add(token);
        if (FfmpegSupports("audio/vnd.dts.hd")) tokens.Add("dts");

        _cached = string.Join(',', tokens.Distinct());
#else
        _cached = "";
#endif
        return _cached;
    }

    /// <summary>Most channels the audio output plays — /api/hls/start and
    /// /api/video maxAudioChannels (≥ 6 keeps 5.1 when the server transcodes
    /// the audio). The HDMI / ARC / eARC sink's PCM channel counts; with
    /// passthrough on "Авто" also 6 when it takes AC-3 / E-AC-3 / DTS
    /// bitstreams (a soundbar on ARC: 2-ch PCM but 5.1 Dolby). No HDMI sink
    /// (built-in speakers, the emulator) → 2.</summary>
    public static int MaxAudioChannels()
    {
#if ANDROID
        try
        {
            var am = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.AudioService)
                     as global::Android.Media.AudioManager;
            if (am is null || !OperatingSystem.IsAndroidVersionAtLeast(23)) return 2;
            int best = 2;
            foreach (var d in am.GetDevices(global::Android.Media.GetDevicesTargets.Outputs) ?? Array.Empty<global::Android.Media.AudioDeviceInfo>())
            {
                int type = (int)d.Type;
                // TYPE_HDMI 9, TYPE_HDMI_ARC 10, TYPE_HDMI_EARC 29.
                if (type is not (9 or 10 or 29)) continue;
                var counts = d.GetChannelCounts() ?? Array.Empty<int>();
                if (counts.Length > 0) best = Math.Max(best, Math.Min(8, counts.Max()));
                if (!TvPlaybackPrefs.PassthroughOff)
                {
                    // ENCODING_AC3 5, E_AC3 6, DTS 7, DTS_HD 8, E_AC3_JOC 18, DOLBY_TRUEHD 14.
                    var enc = d.GetEncodings()?.Select(e => (int)e).ToArray() ?? Array.Empty<int>();
                    if (enc.Any(e => e is 5 or 6 or 7 or 8 or 14 or 18)) best = Math.Max(best, 6);
                }
            }
            return best;
        }
        catch { return 2; }
#else
        return 2;
#endif
    }

#if ANDROID
    /// <summary>"hdr" / "dv" from the display (and, for DV, a decoder).</summary>
    private static string DisplayTokens()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(24)) return "";
            var dm = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.DisplayService)
                     as global::Android.Hardware.Display.DisplayManager;
            var display = dm?.GetDisplay(global::Android.Views.Display.DefaultDisplay);
#pragma warning disable CA1422 // deprecated on 34+ (per-mode HDR types) but still answered
            var types = display?.GetHdrCapabilities()?.GetSupportedHdrTypes()?.Select(t => (int)t).ToArray() ?? Array.Empty<int>();
#pragma warning restore CA1422
            var tokens = new List<string>();
            // HDR_TYPE_DOLBY_VISION 1, HDR10 2, HLG 3, HDR10_PLUS 4.
            if (types.Any(t => t is 2 or 3 or 4)) tokens.Add("hdr");
            if (types.Contains(1) && Decoders().TryGetValue("video/dolby-vision", out var dv) && dv.Count > 0) tokens.Add("dv");
            return string.Join(',', tokens);
        }
        catch { return ""; }
    }
#endif

    /// <summary><c>&amp;clientHevc=1</c> / <c>&amp;clientHevc10=1</c> for
    /// /api/hls/start when this device plays HEVC (Main10) — what the server's
    /// HLS plans copy instead of re-encoding to H.264. Derived from the same
    /// probe as the caps: it used to be sent unconditionally.</summary>
    public static string HevcParams()
    {
        var caps = Get().Split(',');
        return (caps.Contains("hevc") ? "&clientHevc=1" : "") + (caps.Contains("hevc10") ? "&clientHevc10=1" : "");
    }

    /// <summary>False when no decoder that counts for <paramref name="codec"/>
    /// (a server codec name: hevc, h264 …; hardware only for HEVC / AV1 / VP9,
    /// see the class summary) takes a <paramref name="width"/>×<paramref name="height"/>
    /// picture at <paramref name="bitDepth"/> — 4K HEVC Main10 on a 1080p-only
    /// SoC, which Media3 rejects with NO_EXCEEDS_CAPABILITIES (or a software
    /// decoder would play at a few frames a second). The bit depth matters: the
    /// software c2.android.hevc.decoder claims 4096×3072 but decodes Main only.
    /// Unknown codec, no decoder at all or a failed probe → true: let the
    /// player try, the runtime fallback still catches a failure.</summary>
    public static bool CanDecodeSize(string? codec, int width, int height, int bitDepth)
    {
#if ANDROID
        var mime = codec?.ToLowerInvariant() switch
        {
            "h264"                  => "video/avc",
            "hevc"                  => "video/hevc",
            "mpeg4"                 => "video/mp4v-es",
            "mpeg2" or "mpeg2video" => "video/mpeg2",
            "vp8"                   => "video/x-vnd.on2.vp8",
            "vp9"                   => "video/x-vnd.on2.vp9",
            "av1"                   => "video/av01",
            _                       => null,
        };
        if (mime is null || width <= 0 || height <= 0) return true;
        try
        {
            if (!Decoders().TryGetValue(mime, out var all) || all.Count == 0) return true;
            foreach (var d in Usable(mime))
            {
                var vc = d.Caps.VideoCapabilities;
                if (vc is null) return true;
                // Media3 also accepts the rotated size (portrait-encoded video).
                if (!vc.IsSizeSupported(width, height) && !vc.IsSizeSupported(height, width)) continue;
                if (bitDepth >= 10 && !Has10BitProfile(mime, d.Caps)) continue;
                return true;
            }
            global::Android.Util.Log.Info("Animarr.Caps",
                $"{codec} {width}x{height} {bitDepth}-bit exceeds every {(HwOnly.Contains(mime) ? "hardware " : "")}decoder");
            return false;
        }
        catch { return true; }
#else
        return true;
#endif
    }

#if ANDROID
    /// <summary>Codecs a TV-class CPU can't decode in software at video
    /// resolutions: only hardware decoders count.</summary>
    private static readonly HashSet<string> HwOnly = new(StringComparer.Ordinal)
        { "video/hevc", "video/av01", "video/x-vnd.on2.vp9" };

    /// <summary>MediaCodecInfo.CodecProfileLevel.MPEG4ProfileAdvancedSimple.</summary>
    private const int Mpeg4ProfileAdvancedSimple = 0x8000;

    private sealed record VideoDecoder(string Name, bool Hardware,
        global::Android.Media.MediaCodecInfo.CodecCapabilities Caps);

    private static readonly object s_lock = new();
    private static Dictionary<string, List<VideoDecoder>>? s_decoders;

    /// <summary>Every video decoder by mime type (MediaCodecList is read once).</summary>
    private static Dictionary<string, List<VideoDecoder>> Decoders()
    {
        lock (s_lock)
        {
            if (s_decoders is not null) return s_decoders;
            var map = new Dictionary<string, List<VideoDecoder>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
                foreach (var info in list.GetCodecInfos())
                {
                    if (info.IsEncoder) continue;
                    string[] types;
                    try { types = info.GetSupportedTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (!t.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) continue;
                        global::Android.Media.MediaCodecInfo.CodecCapabilities? caps;
                        try { caps = info.GetCapabilitiesForType(t); } catch { continue; }
                        if (caps is null) continue;
                        if (!map.TryGetValue(t, out var l)) map[t] = l = new();
                        l.Add(new VideoDecoder(info.Name, IsHardware(info), caps));
                    }
                }
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("Animarr.Caps", $"MediaCodecList failed: {ex.Message}");
            }
            return s_decoders = map;
        }
    }

    /// <summary>The decoders that count for <paramref name="mime"/>.</summary>
    private static List<VideoDecoder> Usable(string mime)
    {
        if (!Decoders().TryGetValue(mime, out var all)) return new();
        if (HwOnly.Contains(mime)) return all.Where(d => d.Hardware).ToList();
        if (mime == "video/mp4v-es")
            return all.Where(d => d.Hardware && d.Caps.ProfileLevels?.Any(p => (int)p.Profile == Mpeg4ProfileAdvancedSimple) == true).ToList();
        return all;
    }

    /// <summary>API 29+: the platform's flag. Before that, Media3's name rule
    /// (MediaCodecUtil.isSoftwareOnly): OMX.google / c2.android / c2.google,
    /// OMX.ffmpeg, Samsung's *.sw.*, and anything outside the OMX. / c2.
    /// namespaces are software.</summary>
    private static bool IsHardware(global::Android.Media.MediaCodecInfo info)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return info.IsHardwareAccelerated;
        var n = (info.Name ?? "").ToLowerInvariant();
        if (n.StartsWith("arc.", StringComparison.Ordinal)) return true;
        bool software = n.StartsWith("omx.google.", StringComparison.Ordinal)
                        || n.StartsWith("omx.ffmpeg.", StringComparison.Ordinal)
                        || (n.StartsWith("omx.sec.", StringComparison.Ordinal) && n.Contains(".sw."))
                        || n == "omx.qcom.video.decoder.hevcswvdec"
                        || n.StartsWith("c2.android.", StringComparison.Ordinal)
                        || n.StartsWith("c2.google.", StringComparison.Ordinal)
                        || (!n.StartsWith("omx.", StringComparison.Ordinal) && !n.StartsWith("c2.", StringComparison.Ordinal));
        return !software;
    }

    private static bool SupportsType(global::Android.Media.MediaCodecInfo info, string mime)
    {
        try { return info.GetSupportedTypes().Any(t => string.Equals(t, mime, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>The largest common picture any of <paramref name="decoders"/>
    /// takes, as "WxH".</summary>
    private static string? MaxSize(List<VideoDecoder> decoders)
    {
        (int W, int H)[] sizes = { (7680, 4320), (4096, 2160), (3840, 2160), (2560, 1440), (1920, 1088), (1920, 1080), (1280, 720) };
        foreach (var (w, h) in sizes)
            if (decoders.Any(d => d.Caps.VideoCapabilities?.IsSizeSupported(w, h) == true)) return $"{w}x{h}";
        var vc = decoders.Select(d => d.Caps.VideoCapabilities).FirstOrDefault(v => v is not null);
        return vc is null ? null : $"{(int)vc.SupportedWidths!.Upper!}x{(int)vc.SupportedHeights!.Upper!}";
    }

    private static void LogDecoders(string mime, List<VideoDecoder> usable)
    {
        if (!Decoders().TryGetValue(mime, out var all) || all.Count == 0) return;
        var desc = string.Join(", ", all.Select(d =>
            $"{d.Name}[{(d.Hardware ? "hw" : "sw")} ≤{MaxSize(new List<VideoDecoder> { d }) ?? "?"}{(Has10BitProfile(mime, d.Caps) && mime is "video/hevc" or "video/x-vnd.on2.vp9" ? " 10-bit" : "")}]"));
        global::Android.Util.Log.Info("Animarr.Caps", $"{mime}: {desc} → {(usable.Count > 0 ? "advertised" : "not advertised")}");
    }

    private static bool Has10BitProfile(string mime, global::Android.Media.MediaCodecInfo.CodecCapabilities caps)
    {
        // MediaCodecInfo.CodecProfileLevel values: HEVC Main10 / Main10HDR10 /
        // Main10HDR10Plus; VP9 Profile2/3 and their HDR / HDR10+ variants.
        int[] tenBit = mime switch
        {
            "video/hevc"          => new[] { 2, 4096, 8192 },
            "video/x-vnd.on2.vp9" => new[] { 4, 8, 4096, 8192, 16384, 32768 },
            _                     => Array.Empty<int>(),
        };
        if (tenBit.Length == 0) return true;   // no separate 10-bit profile to check
        return caps.ProfileLevels?.Any(p => tenBit.Contains((int)p.Profile)) == true;
    }

    private static bool? _ffmpegAvailable;

    /// <summary>Asks <c>androidx.media3.decoder.ffmpeg.FfmpegLibrary</c> (no C#
    /// binding — the AAR is consumed with Bind="false") whether the native
    /// FFmpeg decoders loaded and cover <paramref name="mime"/>.</summary>
    private static bool FfmpegSupports(string mime)
    {
        try
        {
            // The app's ClassLoader explicitly: Get() runs on a pool thread, and a
            // bare Class.forName there resolves against the system loader, which
            // can't see APK classes (ClassNotFoundException).
            using var cls = global::Java.Lang.Class.ForName("androidx.media3.decoder.ffmpeg.FfmpegLibrary",
                true, global::Android.App.Application.Context.ClassLoader);
            if (_ffmpegAvailable is null)
            {
                var isAvailable = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls.Handle, "isAvailable", "()Z");
                _ffmpegAvailable = global::Android.Runtime.JNIEnv.CallStaticBooleanMethod(cls.Handle, isAvailable);
                var getVersion = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls.Handle, "getVersion", "()Ljava/lang/String;");
                var version = global::Java.Lang.Object.GetObject<global::Java.Lang.String>(
                    global::Android.Runtime.JNIEnv.CallStaticObjectMethod(cls.Handle, getVersion),
                    global::Android.Runtime.JniHandleOwnership.TransferLocalRef)?.ToString();
                global::Android.Util.Log.Info("Animarr.Caps", $"FFmpeg extension available={_ffmpegAvailable} version={version}");
            }
            if (_ffmpegAvailable != true) return false;
            var supportsFormat = global::Android.Runtime.JNIEnv.GetStaticMethodID(cls.Handle, "supportsFormat", "(Ljava/lang/String;)Z");
            using var jmime = new global::Java.Lang.String(mime);
            return global::Android.Runtime.JNIEnv.CallStaticBooleanMethod(cls.Handle, supportsFormat,
                new global::Android.Runtime.JValue(jmime));
        }
        catch (global::System.Exception ex)
        {
            if (_ffmpegAvailable is null)
                global::Android.Util.Log.Warn("Animarr.Caps", $"FFmpeg extension missing: {ex.Message}");
            _ffmpegAvailable = false;
            return false;
        }
    }
#else
    private static bool FfmpegSupports(string mime) => false;
#endif
}
