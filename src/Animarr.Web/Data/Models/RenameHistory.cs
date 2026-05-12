namespace Animarr.Web.Data.Models;

public enum RenameStatus
{
    Pending = 0,
    Renamed = 1,
    Skipped = 2,
    Error = 3,
    Reverted = 4
}

public class RenameHistory
{
    public Guid Id { get; set; }
    public Guid FolderId { get; set; }
    public FolderWatcher Folder { get; set; } = null!;

    public string OriginalPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;

    public RenameStatus Status { get; set; } = RenameStatus.Renamed;
    public string? ErrorMessage { get; set; }

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    public bool IsReverted { get; set; } = false;
}
