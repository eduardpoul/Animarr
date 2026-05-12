using System.ComponentModel.DataAnnotations.Schema;

namespace Animarr.Web.Data.Models;

public enum TorrentRecordState
{
    Downloading = 0,
    Seeding     = 1,
    Paused      = 2,
    Stopped     = 3,
    Error       = 4,
    Metadata    = 5,  // magnet waiting for metadata
    Hashing     = 6,
}

public class TorrentRecord
{
    public Guid Id { get; set; }

    /// <summary>SHA-1 info hash, uppercase hex (40 chars).</summary>
    public string InfoHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Original magnet URI, if added via magnet link.</summary>
    public string? MagnetLink { get; set; }

    /// <summary>Path to the cached .torrent file on disk.</summary>
    public string? TorrentFilePath { get; set; }

    public string SavePath { get; set; } = string.Empty;

    public Guid? FolderWatcherId { get; set; }
    public FolderWatcher? FolderWatcher { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public TorrentRecordState State { get; set; } = TorrentRecordState.Downloading;

    public long TotalSize { get; set; }
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }

    /// <summary>Per-torrent download limit in bytes/s. 0 = no limit.</summary>
    public int DownloadLimit { get; set; } = 0;

    /// <summary>Per-torrent upload limit in bytes/s. 0 = no limit.</summary>
    public int UploadLimit { get; set; } = 0;

    public double? StopSeedingRatio { get; set; }

    /// <summary>If true, stop torrent immediately after download completes (skip seeding).</summary>
    public bool StopAfterDownload { get; set; } = false;

    public bool AutoRename { get; set; } = true;

    /// <summary>If true, files are downloaded directly to SavePath root without subfolder structure.</summary>
    [Column("FlattenSubfolders")]
    public bool SkipSubfolderStructure { get; set; } = false;

    /// <summary>
    /// If true the single root folder inside the torrent is stripped —
    /// files are saved directly into SavePath instead of SavePath/RootFolder/.
    /// Mutually exclusive with CustomRootFolderName.
    /// Applied at metadata-receive time via MonoTorrent file path remapping.
    /// </summary>
    public bool SuppressRootFolder { get; set; } = false;

    /// <summary>
    /// When non-null, renames the single root folder inside the torrent to this value.
    /// Mutually exclusive with SuppressRootFolder.
    /// Applied at metadata-receive time via MonoTorrent file path remapping.
    /// </summary>
    public string? CustomRootFolderName { get; set; }

    public string? ErrorMessage { get; set; }

    public ICollection<TorrentFileSelection> FileSelections { get; set; } = [];
}
