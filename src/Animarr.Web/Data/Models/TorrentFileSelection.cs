namespace Animarr.Web.Data.Models;

/// <summary>
/// Stores which files inside a torrent should be downloaded and at what priority.
/// 0 = DoNotDownload, 1 = Normal, 2 = High
/// </summary>
public class TorrentFileSelection
{
    public Guid Id { get; set; }
    public Guid TorrentId { get; set; }
    public TorrentRecord Torrent { get; set; } = null!;

    /// <summary>Relative path of the file inside the torrent as returned by MonoTorrent.</summary>
    public string FilePath { get; set; } = string.Empty;

    public int Priority { get; set; } = 1;

    /// <summary>
    /// Set to true once the file has been fully downloaded (BitField 100%).
    /// Used by RenameService to decide whether it's safe to move via MoveFileAsync.
    /// </summary>
    public bool IsDownloaded { get; set; } = false;
}
