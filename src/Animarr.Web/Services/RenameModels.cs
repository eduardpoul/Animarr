namespace Animarr.Web.Services;

public enum FileKind
{
    Unknown = 0,
    Video = 1,
    Subtitle = 2,
    Image = 3,
}

/// <summary>Result of parsing a filename against rename patterns.</summary>
public record ParseResult(
    bool IsMatched,
    int? Season,
    int Episode,
    bool IsThumb
);

/// <summary>A single file evaluated for rename, used in preview and history.</summary>
public class RenamePreviewItem
{
    public string OriginalPath { get; init; } = string.Empty;
    public string OriginalName => Path.GetFileName(OriginalPath);
    public string? NewName { get; set; }
    public string? NewPath { get; set; }
    public PreviewStatus Status { get; set; } = PreviewStatus.Pending;
    public string? Reason { get; set; }

    /// <summary>Whether this item is selected for apply (used in preview UI).</summary>
    public bool IsSelected { get; set; } = true;
}

public enum PreviewStatus
{
    Pending = 0,
    WillRename = 1,
    WillSkip = 2,
    AlreadyCorrect = 3,
    Error = 4,
}
