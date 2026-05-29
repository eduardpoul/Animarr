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
    /// "episode" (Tier 1 — same season/episode bucket elsewhere in the tree).
    /// Lets the UI prefer the higher-confidence sidecar when both exist.</summary>
    string MatchTier);
