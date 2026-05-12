namespace Animarr.Web.Data.Models;

public enum IdentificationQueueStatus
{
    Queued     = 0,
    Processing = 1,
    Done       = 2,
    Failed     = 3,
}

/// <summary>
/// Queue of folder identification jobs (LLM + TMDB/MAL lookup).
/// Processed by IdentificationQueueProcessorService one at a time
/// to avoid flooding Ollama with parallel requests.
/// </summary>
public class IdentificationQueue
{
    public Guid Id { get; set; }

    public Guid FolderId { get; set; }
    public FolderWatcher Folder { get; set; } = null!;

    public IdentificationQueueStatus Status { get; set; } = IdentificationQueueStatus.Queued;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Whether to force re-identification even if MediaItem already exists.</summary>
    public bool ForceRefresh { get; set; }

    /// <summary>Step-by-step processing log accumulated during identification.</summary>
    public string? LogDetails { get; set; }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
