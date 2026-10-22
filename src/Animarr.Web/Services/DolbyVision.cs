using System.Globalization;
using System.Text.Json;

namespace Animarr.Web.Services;

/// <summary>
/// Dolby Vision facts of a video stream, from ffprobe's "DOVI configuration
/// record" side data (Matroska BlockAdditionMapping / MP4 dvcC / dvvC).
/// <see cref="Profile"/> -1 = no Dolby Vision.
/// </summary>
public readonly record struct DoviInfo(int Profile, int Level, int Compat)
{
    public static readonly DoviInfo None = new(-1, 0, -1);

    public bool Present => Profile >= 0;

    /// <summary>The base layer alone plays correctly on a non-DV decoder:
    /// bl_signal_compatibility_id 1 = HDR10, 2 = SDR (BT.709), 4 = HLG,
    /// 6 = HDR10 (Blu-ray, the P7 base). 0 = none — P5's IPTPQc2 base is
    /// green / purple without the RPU reshaping.</summary>
    public bool CompatibleBase => Compat is 1 or 2 or 4 or 6;

    /// <summary>P4 / P7: base layer + enhancement layer + RPU. No player falls
    /// back to the base layer by itself (Media3 has no HEVC alternative for
    /// dvhe.07), so non-DV clients get the RPU stripped.</summary>
    public bool DualLayer => Profile is 4 or 7;

    public override string ToString() => Present ? $"P{Profile}.{Compat} L{Level}" : "none";

    /// <summary>Reads one ffprobe stream object. A DOVI record without a
    /// profile field (very old ffprobe) is taken as the common P8.1.</summary>
    public static DoviInfo FromStream(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sd) || sd.ValueKind != JsonValueKind.Array)
            return None;
        foreach (var el in sd.EnumerateArray())
        {
            if (!el.TryGetProperty("side_data_type", out var t)
                || !(t.GetString() ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                continue;
            return new DoviInfo(Int(el, "dv_profile", 8), Int(el, "dv_level", 0),
                Int(el, "dv_bl_signal_compatibility_id", 1));
        }
        return None;
    }

    private static int Int(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;
}

/// <summary>What a stream-copy path does with a Dolby Vision source (master
/// plan §5.4).</summary>
public enum DoviHandling
{
    /// <summary>Not Dolby Vision.</summary>
    None,
    /// <summary>The client decodes DV itself: keep everything, signal it
    /// (<c>dvh1</c> + the dvcC box, which ffmpeg writes only with
    /// <c>-strict unofficial</c>).</summary>
    AsDolbyVision,
    /// <summary>P8.x: ship it as the plain HEVC (or AV1) it is compatible with
    /// — <c>hvc1</c>, no dvcC; the RPU NAL units stay in the bitstream, where
    /// non-DV decoders skip them. Plays as HDR10 / HLG / SDR.</summary>
    AsBaseLayer,
    /// <summary>P7 (and P4) for a non-DV client: <c>-bsf:v dovi_rpu=strip=1</c>
    /// drops the RPUs and the DV configuration, leaving the HDR10 base layer
    /// (the enhancement-layer NAL units are of a type every HEVC decoder
    /// skips). dovi_rpu is in both the dev BtbN build and jellyfin-ffmpeg
    /// 8.1.3; hevc_metadata's remove_dovi is not in the BtbN one.</summary>
    StripToBaseLayer,
    /// <summary>P5 (no compatible base layer) for a non-DV client: a copy
    /// would be green / purple, so the video must be re-encoded — the tone
    /// mapper (libplacebo applies the DV reshaping) turns it into SDR.</summary>
    Reencode,
}

public static class DolbyVision
{
    /// <summary>How a copy (Direct Play's file, Direct Stream's remux, the HLS
    /// fMP4 copy) has to treat <paramref name="dv"/> for this client.</summary>
    public static DoviHandling For(DoviInfo dv, ClientCaps client)
    {
        if (!dv.Present) return DoviHandling.None;
        // A DV client gets single-layer DV untouched. Dual-layer P7 plays on
        // almost nothing that reports "dv" (Safari, most TVs) — strip it too.
        if (dv.DualLayer) return DoviHandling.StripToBaseLayer;
        if (client.DolbyVision) return DoviHandling.AsDolbyVision;
        return dv.CompatibleBase ? DoviHandling.AsBaseLayer : DoviHandling.Reencode;
    }

    /// <summary>
    /// The video-side copy options for <paramref name="handling"/>: the
    /// sample-entry tag (Chrome / Edge refuse HEVC tagged <c>hev1</c> in
    /// (f)MP4 — <c>hvc1</c> is the same bytes), the RPU strip, and
    /// <c>-strict unofficial</c> for a DV output's dvcC box. <paramref name="handling"/>
    /// Reencode can't be honoured by a copy; it is copied as its base layer.
    /// </summary>
    public static IEnumerable<string> CopyVideoArgs(DoviHandling handling, string? videoCodec)
    {
        bool hevc = string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase);
        if (handling == DoviHandling.StripToBaseLayer)
        {
            yield return "-bsf:v"; yield return "dovi_rpu=strip=1";
        }
        if (handling == DoviHandling.AsDolbyVision)
        {
            if (hevc) { yield return "-tag:v"; yield return "dvh1"; }
            yield return "-strict"; yield return "unofficial";
        }
        else if (hevc)
        {
            yield return "-tag:v"; yield return "hvc1";
        }
    }

    /// <summary>RFC 6381 codec string of a DV output (<c>dvh1.PP.LL</c>).</summary>
    public static string CodecsTag(DoviInfo dv)
        => string.Create(CultureInfo.InvariantCulture, $"dvh1.{Math.Max(dv.Profile, 0):D2}.{Math.Max(dv.Level, 1):D2}");
}
