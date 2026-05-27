namespace Animarr.Shared.Models;

/// <summary>
/// Pending or in-flight rename job. The processor service drains this
/// table; the UI only consumes a snapshot of <c>Queued</c> + <c>Processing</c>
/// rows for the "what's happening right now" panel.
/// </summary>
public sealed record RenameQueueEntryDto(
    Guid Id,
    Guid FolderId,
    string FolderLabel,
    string FilePath,
    RenameQueueSource Source,
    DateTime QueuedAt,
    DateTime? ProcessedAt,
    RenameQueueStatus Status,
    string? ErrorMessage,
    int RetryCount);

/// <summary>
/// Completed (or reverted) rename — feeds the History page and the
/// "undo last batch" action.
/// </summary>
public sealed record RenameHistoryEntryDto(
    Guid Id,
    Guid FolderId,
    string FolderLabel,
    string OriginalPath,
    string NewPath,
    RenameStatus Status,
    string? ErrorMessage,
    DateTime ProcessedAt,
    bool IsReverted);

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

/// <summary>
/// Glob mask for files that should be ignored during scan/rename
/// (e.g. <c>fanart.jpg</c>, <c>*.nfo</c>).
/// </summary>
public sealed record IgnoreRuleDto(
    Guid Id,
    string Mask,
    RuleScope Scope,
    Guid? FolderId);
