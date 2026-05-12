namespace Animarr.Web.Data.Models;

public enum RenameQueueStatus
{
    Queued     = 0,   // event received, waiting for processing
    Processing = 1,   // currently being processed
    Done       = 2,   // result written to RenameHistory
    Error      = 3,   // failed after MaxRetries
}

public enum RenameQueueSource
{
    Watcher      = 0,
    AutoDownload = 1,
}

public class RenameQueue
{
    public Guid Id { get; set; }
    public Guid FolderId { get; set; }
    public FolderWatcher Folder { get; set; } = null!;

    public string FilePath { get; set; } = string.Empty;
    public RenameQueueSource Source { get; set; }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    public RenameQueueStatus Status { get; set; } = RenameQueueStatus.Queued;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
}
