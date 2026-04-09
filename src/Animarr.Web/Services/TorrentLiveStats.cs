using MonoTorrent.Client;

namespace Animarr.Web.Services;

/// <summary>
/// Read-only snapshot of a running torrent's live statistics.
/// Updated every 500 ms by TorrentEngineService.
/// </summary>
public record TorrentLiveStats(
    string InfoHash,
    string Name,
    string SavePath,
    TorrentState State,
    double Progress,
    long DownloadRate,
    long UploadRate,
    long Downloaded,
    long Uploaded,
    long TotalSize,
    int Seeds,
    int Peers,
    int DownloadLimit,
    int UploadLimit,
    bool MetadataReceived,
    Guid? FolderWatcherId,
    bool AutoRename
);

/// <summary>
/// Simple tree node for displaying torrent file list.
/// </summary>
public class TorrentFileNode
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public int Depth { get; set; }
    public int Priority { get; set; } = 1;   // 0=DoNotDownload, 1=Normal, 2=High
    public List<TorrentFileNode> Children { get; set; } = [];
}
