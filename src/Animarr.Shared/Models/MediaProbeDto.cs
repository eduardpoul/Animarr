using System.Globalization;
using System.Text.Json;

namespace Animarr.Shared.Models;

/// <summary>
/// The parts of an ffprobe report the UI shows on a title's file card:
/// container, duration, overall bitrate, the main video stream and the
/// audio / subtitle track lists. Built client-side from the raw ffprobe
/// JSON that <c>/api/probe</c> returns (the player reads the same JSON),
/// so the server contract stays unchanged.
/// </summary>
public sealed record MediaProbeDto(
    string? Container,
    double? DurationSec,
    long? BitRate,
    ProbeVideoDto? Video,
    ProbeAudioDto[] Audio,
    ProbeSubtitleDto[] Subtitles)
{
    /// <summary>Parse ffprobe's <c>-show_format -show_streams</c> JSON.</summary>
    public static MediaProbeDto Parse(JsonElement root)
    {
        string? container = null;
        double? duration = null;
        long? bitRate = null;
        if (root.TryGetProperty("format", out var fmt))
        {
            container = Str(fmt, "format_name");
            duration  = Dbl(fmt, "duration");
            bitRate   = Lng(fmt, "bit_rate");
        }

        ProbeVideoDto? video = null;
        var audio = new List<ProbeAudioDto>();
        var subs  = new List<ProbeSubtitleDto>();

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in streams.EnumerateArray())
            {
                var type  = Str(st, "codec_type");
                var codec = Str(st, "codec_name") ?? "";
                var lang  = Tag(st, "language");
                var title = Tag(st, "title");
                switch (type)
                {
                    case "video" when video is null && !Disposition(st, "attached_pic"):
                        video = new ProbeVideoDto(
                            Codec:    codec,
                            Width:    (int)(Lng(st, "width") ?? 0),
                            Height:   (int)(Lng(st, "height") ?? 0),
                            BitDepth: BitDepth(st),
                            Hdr:      HdrFormat(st),
                            Fps:      Rate(Str(st, "avg_frame_rate")) ?? Rate(Str(st, "r_frame_rate")));
                        duration ??= Dbl(st, "duration");
                        break;
                    case "audio":
                        audio.Add(new ProbeAudioDto(
                            Codec:     codec,
                            Profile:   Str(st, "profile"),
                            Channels:  (int)(Lng(st, "channels") ?? 0),
                            Language:  lang,
                            Title:     title,
                            IsDefault: Disposition(st, "default")));
                        break;
                    case "subtitle":
                        subs.Add(new ProbeSubtitleDto(
                            Codec:    codec,
                            Language: lang,
                            Title:    title,
                            IsForced: Disposition(st, "forced")));
                        break;
                }
            }
        }

        return new MediaProbeDto(container, duration, bitRate, video, audio.ToArray(), subs.ToArray());
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Lng(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string? Tag(JsonElement st, string name)
    {
        if (!st.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in tags.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                var s = p.Value.GetString();
                return string.IsNullOrWhiteSpace(s) || s == "und" ? null : s.Trim();
            }
        return null;
    }

    private static bool Disposition(JsonElement st, string flag)
        => st.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object
           && d.TryGetProperty(flag, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;

    private static int BitDepth(JsonElement st)
    {
        if (Lng(st, "bits_per_raw_sample") is long b and > 0) return (int)b;
        var pix = Str(st, "pix_fmt") ?? "";
        if (pix.Contains("12")) return 12;
        if (pix.Contains("10")) return 10;
        return 8;
    }

    private static string? HdrFormat(JsonElement st)
    {
        if (st.TryGetProperty("side_data_list", out var sd) && sd.ValueKind == JsonValueKind.Array)
            foreach (var item in sd.EnumerateArray())
                if ((Str(item, "side_data_type") ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                    return "Dolby Vision";
        return Str(st, "color_transfer") switch
        {
            "smpte2084"    => "HDR10",
            "arib-std-b67" => "HLG",
            _              => null,
        };
    }

    private static double? Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d > 0 && n > 0)
            return n / d;
        return null;
    }
}

public sealed record ProbeVideoDto(string Codec, int Width, int Height, int BitDepth, string? Hdr, double? Fps);

public sealed record ProbeAudioDto(string Codec, string? Profile, int Channels, string? Language, string? Title, bool IsDefault);

public sealed record ProbeSubtitleDto(string Codec, string? Language, string? Title, bool IsForced);
