namespace Animarr.Web.Services;

/// <summary>What a subtitle codec is, for the subtitle endpoints and the
/// track-default picks: bitmap tracks (PGS, VobSub, DVB) can't become text —
/// ffmpeg's text muxers reject them after a full read of the container — and
/// ASS / SSA can be served as the original script.</summary>
public static class SubtitleCodecs
{
    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub", "dvb_teletext",
    };

    /// <summary>True for bitmap subtitle codecs (ffprobe <c>codec_name</c>).</summary>
    public static bool IsImage(string? codec) => codec is not null && Image.Contains(codec);

    /// <summary>True for PGS — the only bitmap codec ffmpeg can write as a
    /// raw <c>.sup</c> (what libbitsub reads).</summary>
    public static bool IsPgs(string? codec)
        => string.Equals(codec, "hdmv_pgs_subtitle", StringComparison.OrdinalIgnoreCase)
           || string.Equals(codec, "pgssub", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for ASS / SSA scripts.</summary>
    public static bool IsAss(string? codec)
        => string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
           || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase);
}
