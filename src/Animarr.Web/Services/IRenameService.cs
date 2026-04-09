using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

public interface IRenameService
{
    /// <summary>Scan folder and return preview items without touching the filesystem.</summary>
    Task<List<RenamePreviewItem>> ScanFolderAsync(Guid folderId, CancellationToken ct = default);

    /// <summary>Apply approved rename items to the filesystem and record history.</summary>
    Task ApplyRenamesAsync(Guid folderId, IEnumerable<RenamePreviewItem> approved, CancellationToken ct = default);

    /// <summary>Process a single newly-appeared file (called by FileSystemWatcher).</summary>
    Task ProcessSingleFileAsync(string filePath, Guid folderId, CancellationToken ct = default);

    /// <summary>Revert a rename using history record.</summary>
    Task<bool> RevertAsync(Guid historyId, CancellationToken ct = default);
}
