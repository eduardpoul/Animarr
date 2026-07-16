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
            // Media3 1.4 bundled extractors (device-independent).
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

        _cached = string.Join(',', tokens.Distinct());
#else
        _cached = "";
#endif
#if ANDROID
        global::Android.Util.Log.Info("Animarr.Caps", $"nativeCaps: {_cached}");
#endif
        return _cached;
    }
}
