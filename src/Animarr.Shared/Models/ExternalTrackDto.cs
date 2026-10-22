namespace Animarr.Shared.Models;

/// <summary>
/// An external (sideload) track that lives on disk next to a video file — a
/// standalone dub audio (.mka/.flac/…) or a sidecar subtitle (.srt/.ass/…) that
/// isn't muxed inside the container. Surfaced by <c>ExternalTrackService</c> and
/// merged into the player's Audio / Subtitles pickers.
///
/// <para><b>Audio</b> tracks are played by restarting the HLS session with the
/// file passed as a second ffmpeg input (<c>-map 1:a:0</c>). <b>Subtitle</b>
/// tracks are converted to WebVTT on the fly by <c>/api/subtitle</c> (it accepts
/// a standalone subtitle file directly via <c>?path=</c> + <c>track=0</c>).</para>
/// </summary>
public sealed record ExternalTrackDto(
    /// <summary>Absolute path on disk. Round-tripped to the playback endpoints,
    /// which re-validate it against the allowed library roots.</summary>
    string Path,
    /// <summary>File name (with extension) — for diagnostics / fallback labels.</summary>
    string FileName,
    /// <summary>Lower-case extension WITHOUT the dot ("mka", "srt"). Shown in
    /// parentheses after the track label in the player picker.</summary>
    string Ext,
    /// <summary>"audio" or "subtitle".</summary>
    string Kind,
    /// <summary>Human-readable label (language + dub/folder hint), already
    /// cleaned for display. The player appends " (ext)" itself.</summary>
    string Label,
    /// <summary>Best-effort ISO-ish language token ("rus", "eng", …) or null.</summary>
    string? Language,
    /// <summary>How the match was made: "sidecar" (Tier 0 — same dir + stem) or
    /// "episode" (Tier 1 — elsewhere in the title's tree: same season/episode,
    /// the video's own name in a sub-folder, or a movie folder's only video).
    /// Lets the UI prefer the higher-confidence sidecar when both exist.</summary>
    string MatchTier,
    /// <summary>ffprobe codec_name of the file's first audio / subtitle stream
    /// ("aac", "opus", "subrip", "ass"); null when not probed.</summary>
    string? Codec = null,
    /// <summary>Audio channel count; null for subtitles / not probed.</summary>
    int? Channels = null,
    /// <summary>Duration in seconds; null when unknown.</summary>
    double? DurationSec = null,
    /// <summary>Delay written into the name (<c>DELAY 500ms</c>, <c>DELAY -120ms</c>)
    /// in milliseconds; null when the name has none.</summary>
    int? DelayMs = null,
    /// <summary>Stable identity of the dub / subtitle group across episodes
    /// (<c>sidecar:ukr</c>, <c>dir:russian dub</c>, …) — what per-profile track
    /// memory keys an external choice on. Opaque to clients.</summary>
    string? GroupKey = null,
    /// <summary>Folder of the file relative to the title root, '/'-separated
    /// ("Russian Dub", "Season 1/Rus Sub/Signs"); "" for the root itself.</summary>
    string? Folder = null,
    /// <summary>Subtitles only: "full", "forced" or "signs" (signs / songs /
    /// надписи in the name or folders). Null for audio.</summary>
    string? SubtitleType = null,
    /// <summary>Subtitles only: true for a bitmap track (<c>.sup</c> PGS,
    /// <c>.idx</c>+<c>.sub</c> VobSub) — needs a bitmap renderer, not
    /// <c>/api/subtitle</c>. Null for audio.</summary>
    bool? Image = null,
    /// <summary>VobSub: the <c>.sub</c> data file that belongs to this
    /// <c>.idx</c> (both fetched with <c>/api/file</c>); null otherwise.</summary>
    string? CompanionPath = null);
