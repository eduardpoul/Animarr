namespace Animarr.Web.Services;

/// <summary>How a re-encode plan turns HDR (PQ / HLG / Dolby Vision) into the
/// 8-bit BT.709 SDR H.264 it outputs. Without one the PQ/BT.2020 samples are
/// just truncated to 8-bit and keep their HDR colour tags — the washed-out,
/// grey picture every re-encoded HDR file used to get.</summary>
public enum HdrToneMapper
{
    /// <summary>SDR source, or tone mapping turned off — frames pass through.</summary>
    None,
    /// <summary>libplacebo on Vulkan (BT.2390, best quality; applies Dolby
    /// Vision RPU reshaping when present). Measured on the prod Vega 11:
    /// 4K HDR10 → 1080p at 1.4× realtime when VAAPI downscales first.</summary>
    Libplacebo,
    /// <summary>jellyfin-ffmpeg's SIMD CPU tone mapper — the fallback when no
    /// usable Vulkan device exists (2.3× realtime for 4K → 1080p on the Vega 11
    /// host, a little more banding than libplacebo).</summary>
    Tonemapx,
    /// <summary>jellyfin-ffmpeg's CUDA tone mapper for the NVENC plan.</summary>
    TonemapCuda,
}

internal static class HdrToneMap
{
    // All mappers target BT.709 limited-range SDR in nv12 — what h264_vaapi /
    // h264_nvenc take — with the BT.2390 EETF.
    public const string LibplaceboFilter =
        "libplacebo=format=nv12:tonemapping=bt.2390:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv";
    public const string TonemapxFilter = "tonemapx=tonemap=bt2390:t=bt709:m=bt709:p=bt709:format=nv12";

    /// <summary>libplacebo that also scales to <paramref name="targetHeight"/>
    /// (0 = keep) — for software-decoded input, where there is no VAAPI
    /// surface to downscale first.</summary>
    public static string LibplaceboScaled(int targetHeight) => targetHeight > 0
        ? LibplaceboFilter.Replace("libplacebo=", $"libplacebo=w=-2:h={targetHeight}:")
        : LibplaceboFilter;
    public const string TonemapCudaFilter = "tonemap_cuda=tonemap=bt2390:t=bt709:m=bt709:p=bt709:format=nv12";

    /// <summary>Encoder-side colour tags for a tone-mapped stream, so players
    /// never see leftover BT.2020/PQ tags on SDR samples.</summary>
    public static readonly string[] Bt709OutputTags =
        { "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709" };

    /// <summary>PQ (HDR10 / HDR10+ / DV P8.1) or HLG transfer, or Dolby
    /// Vision — P5 reports no PQ transfer but still needs tone mapping.</summary>
    public static bool IsHdr(string? colorTransfer, bool hasDolbyVision)
        => hasDolbyVision
           || string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
           || string.Equals(colorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);

    public static string Describe(HdrToneMapper mapper) => mapper switch
    {
        HdrToneMapper.Libplacebo  => "libplacebo / Vulkan",
        HdrToneMapper.Tonemapx    => "tonemapx / CPU",
        HdrToneMapper.TonemapCuda => "tonemap_cuda / NVIDIA",
        _                         => "none",
    };
}
