namespace Animarr.Web.Services;

public enum FileKind
{
    Unknown = 0,
    Video = 1,
    Subtitle = 2,
    Image = 3,
    /// <summary>Standalone audio file (.mka, .flac, .ac3, …) — an external
    /// dub / commentary track that lives next to (or in a sibling sub-folder
    /// of) the video, not muxed inside the container. Surfaced as a selectable
    /// audio track by <see cref="ExternalTrackService"/>.</summary>
    Audio = 4,
}

/// <summary>Result of parsing a filename against rename patterns.</summary>
public record ParseResult(
    bool IsMatched,
    int? Season,
    int Episode,
    bool IsThumb
);
