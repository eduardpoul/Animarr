namespace Animarr.Shared.Requests;

/// <summary>Create or convert a new folder watcher / section.</summary>
public sealed record CreateFolderRequest(
    string Path,
    string Label,
    FolderType FolderType,
    bool IsSection,
    bool FlatSection,
    bool WatchEnabled,
    bool IdentifyEnabled,
    int? Hue,
    string? BackdropPath);

/// <summary>Update mutable fields on an existing folder. Path is locked once the
/// row has been created; rename requires delete + recreate.</summary>
public sealed record UpdateFolderRequest(
    string Label,
    FolderType FolderType,
    bool WatchEnabled,
    bool IdentifyEnabled,
    int? Hue,
    string? BackdropPath);
