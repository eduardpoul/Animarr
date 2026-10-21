namespace Animarr.App.Services;

/// <summary>
/// Probes what the DEVICE can actually decode and turns it into the
/// comma-separated token list <c>/api/hls/start?nativeCaps=</c> expects.
/// The server uses it to pick the widest playback tier the device can take —
/// e.g. an AVI/XviD/AC3 rip Direct Plays on a TV whose SoC decodes MPEG4-ASP,
/// instead of burning the server CPU on the transcode a browser would need.
///
/// Containers are static per Media3 version (the bundled extractors demux
/// MP4/MKV/WebM/TS/PS/AVI/FLV/Ogg/WAV regardless of device); codecs come from
/// <c>MediaCodecList</c> at runtime, so a phone without an AC3 license simply
/// doesn't advertise "ac3" and the server remuxes audio for it.
/// </summary>
internal static class TvCodecCaps
{
    private static string? _cached;

    public static string Get()
    {
        if (_cached is not null) return _cached;
#if ANDROID
        var tokens = new List<string>
        {
            // Media3 bundled extractors (device-independent).
            "mp4", "m4v", "mov", "mkv", "webm", "ts", "m2ts", "avi", "flv", "ogg", "wav",
        };

        // mime → server token. Decoder presence probed per-device below.
        (string Mime, string Token)[] probes =
        {
            ("video/avc",           "h264"),
            ("video/hevc",          "hevc"),
            ("video/mp4v-es",       "mpeg4"),
            ("video/mpeg2",         "mpeg2"),
            ("video/x-vnd.on2.vp8", "vp8"),
            ("video/x-vnd.on2.vp9", "vp9"),
            ("video/av01",          "av1"),
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
            var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
            var infos = list.GetCodecInfos();
            bool hevc10 = false;
            foreach (var (mime, token) in probes)
            {
                var supported = false;
                foreach (var info in infos)
                {
                    if (info.IsEncoder) continue;
                    string[] types;
                    try { types = info.GetSupportedTypes(); } catch { continue; }
                    if (!types.Any(t => string.Equals(t, mime, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    supported = true;
                    // HEVC Main10 → the separate "hevc10" token (10-bit HDR gate).
                    if (token == "hevc")
                    {
                        try
                        {
                            var caps = info.GetCapabilitiesForType(mime);
                            if (caps?.ProfileLevels?.Any(p =>
                                    (int)p.Profile == (int)global::Android.Media.MediaCodecProfileType.Hevcprofilemain10) == true)
                                hevc10 = true;
                        }
                        catch { }
                    }
                    break;
                }
                if (supported) tokens.Add(token);
            }
            if (hevc10) tokens.Add("hevc10");
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
        foreach (var (mime, token) in probes)
            if (mime.StartsWith("audio/", StringComparison.Ordinal) && FfmpegSupports(mime))
                tokens.Add(token);
        if (FfmpegSupports("audio/vnd.dts.hd")) tokens.Add("dts");

        _cached = string.Join(',', tokens.Distinct());
#else
        _cached = "";
#endif
#if ANDROID
        global::Android.Util.Log.Info("Animarr.Caps", $"nativeCaps: {_cached}");
#endif
        return _cached;
    }

    /// <summary>False when the device has decoders for <paramref name="codec"/>
    /// (a server codec name: hevc, h264 …) but none takes a
    /// <paramref name="width"/>×<paramref name="height"/> picture at
    /// <paramref name="bitDepth"/> — 4K HEVC Main10 on a 1080p-only SoC, which
    /// Media3 rejects with NO_EXCEEDS_CAPABILITIES. The bit depth matters: the
    /// software c2.android.hevc.decoder claims 4096×3072 but decodes Main only.
    /// Unknown codec or a failed probe → true: let the player try, the runtime
    /// fallback still catches a failure.</summary>
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
            bool anyDecoder = false;
            var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
            foreach (var info in list.GetCodecInfos())
            {
                if (info.IsEncoder) continue;
                string[] types;
                try { types = info.GetSupportedTypes(); } catch { continue; }
                if (!types.Any(t => string.Equals(t, mime, StringComparison.OrdinalIgnoreCase)))
                    continue;
                anyDecoder = true;
                var caps = info.GetCapabilitiesForType(mime);
                var video = caps?.VideoCapabilities;
                if (video is null) return true;
                // Media3 also accepts the rotated size (portrait-encoded video).
                if (!video.IsSizeSupported(width, height) && !video.IsSizeSupported(height, width))
                    continue;
                if (bitDepth >= 10 && !Has10BitProfile(mime, caps!)) continue;
                return true;
            }
            if (anyDecoder)
                global::Android.Util.Log.Info("Animarr.Caps", $"{codec} {width}x{height} exceeds every decoder");
            return !anyDecoder;
        }
        catch { return true; }
#else
        return true;
#endif
    }

#if ANDROID
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
