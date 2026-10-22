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

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1",
        "sami", "realtext", "mpl2", "pjs", "vplayer", "jacosub", "stl",
    };

    /// <summary>True for the text codecs ffmpeg converts to WebVTT / SRT
    /// (an unknown codec counts as not text: it could fail a shared run).</summary>
    public static bool IsText(string? codec) => codec is not null && Text.Contains(codec);

    /// <summary>True for ASS / SSA scripts.</summary>
    public static bool IsAss(string? codec)
        => string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
           || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase);
}
