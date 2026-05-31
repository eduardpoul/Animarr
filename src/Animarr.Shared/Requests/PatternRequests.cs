namespace Animarr.Shared.Requests;

/// <summary>Create or update a rename pattern. Built-in patterns are read-only
/// at the API level (server rejects writes when <c>IsBuiltIn</c> is set).</summary>
public sealed record UpsertPatternRequest(
    string Name,
    string Pattern,
    PatternScope Scope,
    bool IsExcluded,
    Guid? GlobalPatternId,
    int Priority,
    FolderType? ApplicableTo,
    Guid? FolderId);
