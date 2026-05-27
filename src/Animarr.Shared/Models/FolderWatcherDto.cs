namespace Animarr.Shared.Models;

/// <summary>
/// Server-side filesystem folder that Animarr is responsible for indexing.
/// Two flavours: ordinary leaf folders (one media item per folder) and
/// "sections" — parent folders whose children are auto-imported as their
/// own watchers.
/// </summary>
public sealed record FolderWatcherDto
{
    public Guid Id { get; init; }

    /// <summary>Absolute path on the server — outside the container this is
    /// the bind-mount source; inside it's the canonical /media/... path.</summary>
    public string Path  { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    public bool       WatchEnabled    { get; init; } = true;
    public FolderType FolderType      { get; init; } = FolderType.Auto;
    public bool       RenameEnabled   { get; init; } = true;
    public bool       IdentifyEnabled { get; init; } = true;

    /// <summary>True for section-root rows (children get auto-created underneath).</summary>
    public bool  IsSection       { get; init; }
    /// <summary>True only when <see cref="IsSection"/> is true and the section watches single
    /// video files at its root rather than subfolders.</summary>
    public bool  FlatSection     { get; init; }
    /// <summary>For flat-section leaf rows: the path of the single video file this row represents.</summary>
    public string? SingleFilePath { get; init; }
    /// <summary>FK back to the section row that auto-created this leaf, when applicable.</summary>
    public Guid?  ParentSectionId { get; init; }

    public DateTime  CreatedAt     { get; init; }
    public DateTime? LastScannedAt { get; init; }

    public int?    Hue          { get; init; }
    public string? BackdropPath { get; init; }
}
