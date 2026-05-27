namespace Animarr.Shared.Requests;

public sealed record UpsertMediaTagRequest(
    string Name,
    string? Color,
    int SortOrder,
    bool IsAutoTag);
