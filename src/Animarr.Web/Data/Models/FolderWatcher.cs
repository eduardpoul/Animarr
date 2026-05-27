namespace Animarr.Web.Data.Models;

public enum FolderType
{
    Auto   = 0,
    Series = 1,
    Movie  = 2,
}

public class FolderWatcher
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool WatchEnabled { get; set; } = true;
    public FolderType FolderType { get; set; } = FolderType.Auto;

    /// <summary>
    /// When true, files downloaded into this folder will be auto-renamed
    /// using the configured RenameService after download completes.
    /// Also controls whether FileSystemWatcher triggers renames on new files.
    /// </summary>
    public bool RenameEnabled { get; set; } = true;

    /// <summary>
    /// When true this record is a "section" — a parent folder whose immediate
    /// subdirectories have been (or will be) auto-imported as individual child
    /// FolderWatcher entries.
    /// </summary>
    public bool IsSection { get; set; } = false;

    /// <summary>
    /// When true, this folder (and any auto-created children) will be submitted
    /// for identification against external sources (TMDB, MAL, etc.).
    /// Set to false for folders whose content type has no supported source (e.g. manga).
    /// </summary>
    public bool IdentifyEnabled { get; set; } = true;

    /// <summary>
    /// When true and <see cref="IsSection"/> is true, the section watches for video files
    /// directly in its root directory instead of subdirectories. Each discovered video file
    /// is auto-registered as a child <see cref="FolderWatcher"/> with <see cref="SingleFilePath"/> set.
    /// Intended for flat movie libraries where each movie is a single file with no subfolder.
    /// </summary>
    public bool FlatSection { get; set; } = false;

    /// <summary>
    /// For flat-section children: absolute path to the single video file this entry represents.
    /// <see cref="Path"/> is set to the parent section directory (shared among siblings).
    /// When null, this entry represents a regular directory.
    /// </summary>
    public string? SingleFilePath { get; set; }

    /// <summary>
    /// Points to the section FolderWatcher that auto-created this record.
    /// Null for manually-added folders and for section roots.
    /// </summary>
    public Guid? ParentSectionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }

    /// <summary>Hue in degrees (0..360) used for the section card tint and folder chip color
    /// in the catalog filter bar. Deterministically derived from Label hash on create;
    /// user-editable from Settings → Root folders.</summary>
    public int? Hue { get; set; }

    /// <summary>Absolute path to a backdrop image used by the section card in the catalog.
    /// When null, the UI falls back to the first identified child MediaItem's FanartPath.</summary>
    public string? BackdropPath { get; set; }

    public ICollection<RenamePattern> Patterns { get; set; } = [];
    public ICollection<IgnoreRule> IgnoreRules { get; set; } = [];
    public ICollection<RenameHistory> History { get; set; } = [];
}
