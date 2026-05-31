namespace Animarr.Shared.Models;

/// <summary>
/// Regex pattern that extracts season+episode tokens from filenames.
/// Built-in patterns are seeded at startup; user-defined patterns get
/// stored alongside and can override or augment them.
/// </summary>
public sealed record RenamePatternDto
{
    public Guid   Id              { get; init; }
    public string Name            { get; init; } = string.Empty;
    public string Pattern         { get; init; } = string.Empty;
    public PatternScope Scope     { get; init; } = PatternScope.Global;
    public bool   IsExcluded      { get; init; }
    public Guid?  GlobalPatternId { get; init; }
    public int    Priority        { get; init; } = 100;
    public bool   IsBuiltIn       { get; init; }
    public FolderType? ApplicableTo { get; init; }
    public Guid?  FolderId        { get; init; }
}
