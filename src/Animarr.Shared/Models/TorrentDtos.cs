namespace Animarr.Shared.Models;

/// <summary>
/// Persisted torrent row — long-lived facts kept in the database. Live
/// counters (download rate, seeders) come from <see cref="TorrentLiveStatsDto"/>
/// over the SignalR hub.
/// </summary>
public sealed record TorrentRecordDto
{
    public Guid   Id        { get; init; }
    /// <summary>SHA-1 info hash, uppercase hex (40 chars).</summary>
    public string InfoHash  { get; init; } = string.Empty;
    public string Name      { get; init; } = string.Empty;
    public string? MagnetLink { get; init; }
    public string? TorrentFilePath { get; init; }
    public string SavePath  { get; init; } = string.Empty;

    public Guid? FolderWatcherId { get; init; }

    public DateTime  AddedAt     { get; init; }
    public DateTime? CompletedAt { get; init; }

    public TorrentRecordState State { get; init; }

    public long TotalSize  { get; init; }
    public long Downloaded { get; init; }
    public long Uploaded   { get; init; }

    public int DownloadLimit { get; init; }
    public int UploadLimit   { get; init; }

    public double? StopSeedingRatio { get; init; }
    public bool    StopAfterDownload { get; init; }
    public bool    AutoRename { get; init; } = true;

    public bool    SkipSubfolderStructure { get; init; }
    public bool    SuppressRootFolder { get; init; }
    public string? CustomRootFolderName { get; init; }

    public string? ErrorMessage { get; init; }

    public TorrentFileSelectionDto[] FileSelections { get; init; }
        = Array.Empty<TorrentFileSelectionDto>();
}

/// <summary>
/// Live counters for a running torrent — replaced wholesale on every
/// SignalR push, no merge logic needed.
/// </summary>
public sealed record TorrentLiveStatsDto(
    string InfoHash,
    string Name,
    string SavePath,
    /// <summary>Mirror of MonoTorrent's TorrentState as a string so we don't
    /// take a dep on its enum in Animarr.Shared.</summary>
    string State,
    double Progress,
    long DownloadRate,
    long UploadRate,
    long Downloaded,
    long Uploaded,
    long TotalSize,
    int  Seeds,
    int  Peers,
    int  DownloadLimit,
    int  UploadLimit,
    bool MetadataReceived,
    Guid? FolderWatcherId,
    bool AutoRename);

/// <summary>
/// Node in the torrent's file tree shown in TorrentEdit. <c>Children</c> is
/// empty for files; <c>IsDir</c> + <c>Path</c> identify the parent slot.
/// </summary>
public sealed record TorrentFileNodeDto
{
    public string Path  { get; init; } = string.Empty;
    public string Name  { get; init; } = string.Empty;
    public bool   IsDir { get; init; }
    public long   Size  { get; init; }
    public int    Depth { get; init; }
    /// <summary>0=DoNotDownload, 1=Normal, 2=High.</summary>
    public int    Priority { get; init; } = 1;
    public List<TorrentFileNodeDto> Children { get; init; } = new();
}

public sealed record TorrentFileSelectionDto(
    Guid Id,
    Guid TorrentId,
    string FilePath,
    int Priority,
    bool IsDownloaded);

/// <summary>One entry inside a parsed .torrent file (no engine state).
/// Returned by <c>POST /api/torrents/parse</c> so the Add drawer can
/// preview the manifest before the user commits.</summary>
public sealed record ParsedTorrentFileDto(string Path, long Length);

/// <summary>Result of <c>POST /api/torrents/parse</c>.</summary>
public sealed record ParsedTorrentDto(
    string Name,
    long TotalSize,
    ParsedTorrentFileDto[] Files);

/// <summary>
/// Singleton engine-wide torrent settings. Saved via PUT to
/// <c>/api/torrent-config</c>.
/// </summary>
public sealed record TorrentConfigDto
{
    public int GlobalDownloadLimit { get; init; }
    public int GlobalUploadLimit   { get; init; }

    public int ListenPort     { get; init; } = 6881;
    public int MaxConnections { get; init; } = 200;

    public bool EnableDHT  { get; init; } = true;
    public bool EnableLSD  { get; init; } = true;
    public bool EnableUPnP { get; init; } = true;

    public bool   StopSeedingAfterDone     { get; init; }
    public double StopSeedingRatio         { get; init; } = 2.0;
    public bool   AutoRenameAfterDownload  { get; init; } = true;
    public bool   DefaultStopAfterDownload { get; init; }

    public string CacheDirectory { get; init; } = "/app/data/torrent-cache";
}
