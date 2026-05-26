namespace Animarr.Shared.Models;

/// <summary>
/// Row from the identification job queue — what the processor is currently
/// chewing on, plus historical successes / failures the UI surfaces in the
/// status sidebar.
/// </summary>
public sealed record IdentificationQueueEntryDto(
    Guid Id,
    Guid FolderId,
    string FolderLabel,
    IdentificationQueueStatus Status,
    int RetryCount,
    string? ErrorMessage,
    bool ForceRefresh,
    string? LogDetails,
    DateTime QueuedAt,
    DateTime? ProcessedAt);
