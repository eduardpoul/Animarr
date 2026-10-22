namespace Animarr.Shared.Models;

// Per-file playback facts the players read next to /api/probe: chapters,
// black-bar crop, loudness, subtitle tracks and fonts. All cached per file
// version (path + mtime + size) on the server.

/// <summary>One container chapter — <c>GET /api/chapters?path=</c> answers an
/// array of these, in start order (empty when the file has none).</summary>
/// <param name="Index">Position in the list (0-based).</param>
/// <param name="Title">The chapter's title tag ("Opening", "Part A") or null.</param>
/// <param name="StartSec">Start in seconds of the file's timeline.</param>
/// <param name="EndSec">End in seconds; a missing end is the next chapter's
/// start (the file's duration for the last one).</param>
public sealed record ChapterDto(int Index, string? Title, double StartSec, double EndSec);

/// <summary><c>GET /api/media-facts?path=[&amp;wait=N]</c>: measured facts of a
/// file, beyond what /api/probe says.</summary>
/// <param name="DurationSec">The container's duration.</param>
/// <param name="Video">The main video (null for audio-only files).</param>
public sealed record MediaFactsDto(double? DurationSec, VideoFactsDto? Video);

/// <summary>Picture geometry, including black bars baked into the frame.</summary>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="SampleAspect">Pixel aspect ("1:1", "4:3"); null when unknown
/// (treat as square).</param>
/// <param name="DisplayAspect">Width / height of the whole frame on screen
/// (pixel aspect applied), e.g. 1.7778.</param>
/// <param name="Crop">The picture inside the bars, in coded pixels; null when
/// there are no bars, while detection is pending or when it couldn't tell.</param>
/// <param name="ContentAspect">Width / height of the picture without its
/// bars on screen (= <paramref name="DisplayAspect"/> without bars), e.g. 2.4.</param>
/// <param name="Letterbox">Bars above and below.</param>
/// <param name="Pillarbox">Bars left and right.</param>
/// <param name="CropStatus">"detected" (bars found), "none" (no bars),
/// "pending" (still measuring — ask again), "unknown" (could not tell, e.g.
/// every sampled frame was black).</param>
public sealed record VideoFactsDto(
    int Width,
    int Height,
    string? SampleAspect,
    double DisplayAspect,
    CropRectDto? Crop,
    double ContentAspect,
    bool Letterbox,
    bool Pillarbox,
    string CropStatus);

public sealed record CropRectDto(int X, int Y, int Width, int Height);

/// <summary><c>GET /api/loudness?path=&amp;track=N[&amp;wait=S]</c>: EBU R128
/// loudness of audio track a:N (or of an external audio file, track 0).</summary>
/// <param name="Status">"measured", "pending" (measuring in the background —
/// ask again later; a full decode of the track takes a while) or "unknown"
/// (could not be measured).</param>
/// <param name="IntegratedLufs">Integrated loudness, LUFS (e.g. -23.4).</param>
/// <param name="LoudnessRangeLu">Loudness range, LU.</param>
/// <param name="TruePeakDbtp">True peak, dBTP.</param>
/// <param name="GainDb">Suggested playback gain: towards -16 LUFS, limited so
/// the true peak stays at or below -1 dBTP (may be negative = attenuate).</param>
public sealed record LoudnessDto(int Track, string Status, double? IntegratedLufs, double? LoudnessRangeLu,
    double? TruePeakDbtp, double? GainDb);

/// <summary><c>GET /api/subtitle/tracks?path=[&amp;analyze=1]</c>: the subtitle
/// tracks of a video (or of a standalone subtitle file — then one track,
/// index 0) and the fonts attached to it.</summary>
/// <param name="Fonts">Font attachments (filled when the file has an ASS / SSA
/// track; extracted on the first call per file version).</param>
public sealed record SubtitleTracksDto(IReadOnlyList<SubtitleTrackInfoDto> Tracks, IReadOnlyList<SubtitleFontDto> Fonts);

/// <param name="Index">s:N — what <c>/api/subtitle?track=</c> takes.</param>
/// <param name="Codec">ffprobe codec_name ("subrip", "ass", "hdmv_pgs_subtitle").</param>
/// <param name="Kind">"text" (convertible to WebVTT / ASS) or "image" (PGS,
/// VobSub, DVB — needs a bitmap renderer or burn-in).</param>
/// <param name="Ass">ASS / SSA tracks: the script analysis; null when not
/// analyzed yet (ask with <c>analyze=1</c>, which copies the uncached ASS
/// tracks out in one read of the container) or for other codecs.</param>
/// <param name="VttUrl">Text tracks: WebVTT URL (relative; append the media
/// token like any media URL).</param>
/// <param name="AssUrl">Text tracks: ASS URL — the ORIGINAL script for an ASS /
/// SSA source (styles, typesetting intact), a default-styled conversion
/// otherwise.</param>
/// <param name="SupUrl">PGS tracks: the raw <c>.sup</c> stream for a bitmap
/// renderer (libbitsub) — <c>/api/subtitle/sup?path=…&amp;track=N</c>.</param>
public sealed record SubtitleTrackInfoDto(
    int Index,
    string? Codec,
    string Kind,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    AssInfoDto? Ass,
    string? VttUrl,
    string? AssUrl,
    string? SupUrl = null);

/// <param name="Complex">True when a plain WebVTT rendering loses what
/// matters — use a libass renderer (JASSUB) for it.</param>
/// <param name="Reasons">"pos", "move", "org", "animation", "karaoke",
/// "clip", "fade", "rotation", "shear", "drawing", "many-styles".</param>
/// <param name="Fonts">Font names the script asks for (styles + \fn).</param>
public sealed record AssInfoDto(int Styles, int Events, bool Complex, IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Fonts);

/// <summary>One font attachment — <c>GET /api/subtitle/fonts?path=</c> answers
/// an array of these.</summary>
/// <param name="Id">Absolute stream index of the attachment.</param>
/// <param name="Family">Typographic family ("Arial") — the key JASSUB's
/// <c>availableFonts</c> map wants (lower-cased).</param>
/// <param name="Url">Relative URL of the font file
/// (<c>/api/subtitle/font?path=…&amp;id=N</c>).</param>
public sealed record SubtitleFontDto(int Id, string FileName, string? MimeType, long? SizeBytes,
    string? Family, string? FullName, string? PostScriptName, string Url);
