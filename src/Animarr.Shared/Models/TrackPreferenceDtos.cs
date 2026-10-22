namespace Animarr.Shared.Models;

// Per-profile track memory (playback plan §6.5 / task 2.2). Routes:
//   GET    /api/me/track-prefs/{mediaItemId}?season=N  → TrackPrefsDto (the stored row)
//   PUT    /api/me/track-prefs/{mediaItemId}?season=N  ← TrackPrefsUpdateRequest → TrackPrefsDto
//   DELETE /api/me/track-prefs/{mediaItemId}?season=N  → 204
//   GET    /api/me/track-prefs/resolve?path=…          → TrackDefaultsDto
// No season = the title row (a movie, or every season of a series).
// /api/hls/start answers the same TrackDefaultsDto as "trackDefaults".

/// <summary>
/// Partial update of a title's track memory: only the members that are
/// present (non-null) change; values are stored as given — an explicit 0 is
/// remembered as 0. Forgetting a facet (back to the defaults) is
/// <paramref name="Clear"/>. The client names WHAT it picked on WHICH file;
/// the server derives the meaning (language, title, dub group) itself.
/// </summary>
/// <param name="Path">The video the choice was made on — required with
/// <paramref name="Audio"/> / <paramref name="Subtitle"/> choices other than
/// "off" / "auto", and must belong to the title.</param>
/// <param name="SubtitleOffsetMs">Subtitle delay (positive = later), ±600000.</param>
/// <param name="AudioOffsetMs">Sync offset (ms, + = the sound later, ±3000) of
/// the audio picked in this request (or, without one, of the audio
/// <paramref name="Path"/> currently resolves to, else the remembered audio).
/// 0 is stored — it overrides a dub's <c>DELAY Nms</c>.</param>
/// <param name="Aspect">Aspect / zoom preset (≤ 32 chars of letters, digits,
/// ':', '.', '-', '_'); "" clears (kept for older clients).</param>
/// <param name="Clear">Facets to forget, applied after the values above (so
/// clearing wins): "audio", "subtitle", "audioOffset" (of the same audio
/// <paramref name="AudioOffsetMs"/> would target — a dub falls back to its
/// DELAY tag), "audioOffsets" (all of them), "subtitleOffset", "aspect".
/// <c>audio.kind = "auto"</c> / <c>subtitle.kind = "auto"</c> still forget too.</param>
public sealed record TrackPrefsUpdateRequest(
    string? Path = null,
    TrackChoiceRequest? Audio = null,
    TrackChoiceRequest? Subtitle = null,
    int? SubtitleOffsetMs = null,
    int? AudioOffsetMs = null,
    string? Aspect = null,
    IReadOnlyList<string>? Clear = null);

/// <summary>One track pick.</summary>
/// <param name="Kind">"embedded" (+ <paramref name="Index"/>), "external"
/// (+ <paramref name="Path"/>), "off" (subtitles only) or "auto" (forget the
/// choice — the profile's defaults apply again).</param>
/// <param name="Index">Embedded: the type-relative index — the N of ffmpeg's
/// <c>0:a:N</c> / <c>0:s:N</c>, the same number <c>audioTrackIndex</c> and
/// <c>/api/subtitle?track=</c> take.</param>
/// <param name="Path">External: the file's path as <c>/api/external-tracks</c>
/// returned it.</param>
public sealed record TrackChoiceRequest(string Kind, int? Index = null, string? Path = null);

/// <summary>A stored memory row. Facets are null when not remembered.</summary>
public sealed record TrackPrefsDto(
    Guid MediaItemId,
    int? Season,
    TrackAudioPrefDto? Audio,
    TrackSubtitlePrefDto? Subtitle,
    int? SubtitleOffsetMs,
    IReadOnlyDictionary<string, int> AudioOffsets,
    string? Aspect,
    DateTime? UpdatedAt);

/// <summary>A remembered audio choice, by meaning.</summary>
/// <param name="Kind">"embedded" | "external".</param>
/// <param name="Language">ISO 639-2 ("rus", "jpn") or null.</param>
public sealed record TrackAudioPrefDto(
    string Kind, string? Language, string? Title, string? Codec, int? Channels, string? GroupKey);

/// <summary>A remembered subtitle choice, by meaning.</summary>
/// <param name="Kind">"embedded" | "external" | "off".</param>
/// <param name="Type">"full" | "forced" | "signs".</param>
public sealed record TrackSubtitlePrefDto(
    string Kind, string? Language, string? Type, string? Title, string? GroupKey);

/// <summary>
/// The tracks a playback of one file should start with, resolved per facet:
/// the title's memory (season row before title row) → the profile settings
/// (audio language; subtitle language / "Off" / "Forced") → the container's
/// default / forced flags → the first track.
/// </summary>
/// <param name="MediaItemId">The title the file belongs to (what a PUT
/// targets); null when the file is outside any identified title.</param>
/// <param name="Season">The file's season (null for a movie / unknown).</param>
/// <param name="Audio">Null when the file has no audio at all.</param>
/// <param name="Subtitle">Never null: kind "off" when nothing should show.</param>
/// <param name="AudioOffsetMs">Sync offset of <paramref name="Audio"/> (ms,
/// + = the sound later, ±3000): the remembered one, else an external dub's
/// <c>DELAY Nms</c> from its name, else 0 — see <paramref name="AudioOffsetSource"/>.</param>
/// <param name="AudioOffsetSource">"memory" (remembered for this audio — 0
/// included), "delay" (the dub's DELAY tag) or "none".</param>
/// <param name="AudioOffsetApplied">On a <c>/api/hls/start</c> answer with
/// <c>usePrefs=1</c> and no explicit audio choice: true when the server added
/// <paramref name="AudioOffsetMs"/> to the stream's sync itself (on top of the
/// hw / sw baseline the client sent) — don't add it again. False when the
/// client chose the audio explicitly or the plan takes no offset (Direct Play,
/// Direct Stream, TS copy of the file's own audio). Always false from
/// <c>/resolve</c>.</param>
public sealed record TrackDefaultsDto(
    Guid? MediaItemId,
    int? Season,
    TrackDefaultAudioDto? Audio,
    TrackDefaultSubtitleDto Subtitle,
    int SubtitleOffsetMs,
    int AudioOffsetMs,
    string? Aspect,
    string AudioOffsetSource = "none",
    bool AudioOffsetApplied = false);

/// <param name="Kind">"embedded" | "external".</param>
/// <param name="Index">Embedded: a:N (what <c>audioTrackIndex</c> takes).</param>
/// <param name="Path">External: the dub file (what <c>externalAudio</c> takes).</param>
/// <param name="DelayMs">External: <c>DELAY Nms</c> from the file name.</param>
/// <param name="Source">Why this track: "memory-season", "memory", "profile",
/// "default" (container flag) or "first".</param>
/// <param name="Applied">On a <c>/api/hls/start</c> answer: true when the
/// stream the server returned already carries this audio (HLS / Direct Stream
/// mapped it); false when the client must select it itself (Direct Play) or
/// asked for something else explicitly. Always false from <c>/resolve</c>.</param>
public sealed record TrackDefaultAudioDto(
    string Kind,
    int? Index,
    string? Path,
    string? Language,
    string? Title,
    string? Codec,
    int? Channels,
    string? GroupKey,
    string? Label,
    int? DelayMs,
    string Source,
    bool Applied = false);

/// <param name="Kind">"embedded" | "external" | "off".</param>
/// <param name="Index">Embedded: s:N (what <c>/api/subtitle?track=</c> takes).</param>
/// <param name="Path">External: the subtitle file.</param>
/// <param name="Type">"full" | "forced" | "signs".</param>
/// <param name="Source">"memory-season", "memory", "profile", "default",
/// "first" or "none" (the file has no subtitles).</param>
public sealed record TrackDefaultSubtitleDto(
    string Kind,
    int? Index,
    string? Path,
    string? Language,
    string? Type,
    string? Title,
    string? Codec,
    string? GroupKey,
    string? Label,
    string Source);
