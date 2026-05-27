namespace Animarr.Shared.Models;

/// <summary>
/// One entry in the server-side filesystem-browser used by the
/// SectionFolderDialog's drill-down picker. <see cref="Path"/> is the
/// absolute path on the server; <see cref="Name"/> is the leaf-name for
/// display.
///
/// When <see cref="Path"/> is empty, the entry represents one of the
/// well-known roots (e.g. <c>/mnt</c>, <c>/Pool-D1/Media</c>) — clicking
/// it loads its children.
/// </summary>
public sealed record FolderBrowseEntryDto(
    string Path,
    string Name,
    bool IsRoot);
