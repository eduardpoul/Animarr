namespace Animarr.Shared.Models;

/// <summary>
/// User-defined collection tag (renders as a tab on the catalog).
/// </summary>
public sealed record MediaTagDto(
    Guid Id,
    string Name,
    string? Color,
    int SortOrder,
    bool IsAutoTag,
    int ItemCount);
