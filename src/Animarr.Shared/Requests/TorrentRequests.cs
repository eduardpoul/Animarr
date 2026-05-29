namespace Animarr.Shared.Requests;

/// <summary>Magnet URI + destination folder. The engine resolves metadata on its own.
/// <para>DownloadLimitKBps / UploadLimitKBps in KB/s; 0 = unlimited (default).
/// Added in v5 — earlier add endpoints hardcoded both to 0, leaving the
/// throttle slider out of reach until after the torrent finished probing.
/// Optional with sensible default so older clients still work.</para></summary>
public sealed record AddMagnetRequest(
    string MagnetLink,
    Guid?  FolderWatcherId,
    bool   AutoRename,
    bool   StopAfterDownload,
    bool   SkipSubfolderStructure,
    bool   SuppressRootFolder,
    string? CustomRootFolderName,
    int    DownloadLimitKBps = 0,
    int    UploadLimitKBps   = 0);

/// <summary>.torrent file uploaded as base64 (the bytes are small enough that
/// streaming/multipart isn't worth the complexity here).</summary>
public sealed record AddTorrentFileRequest(
    string Filename,
    string Base64Content,
    Guid?  FolderWatcherId,
    bool   AutoRename,
    bool   StopAfterDownload,
    bool   SkipSubfolderStructure,
    bool   SuppressRootFolder,
    string? CustomRootFolderName,
    int    DownloadLimitKBps = 0,
    int    UploadLimitKBps   = 0,
    /// <summary>File paths (as listed in the parsed manifest — MonoTorrent's
    /// <c>Torrent.Files[].Path</c>) the user UNCHECKED in the Add-drawer tree.
    /// The engine stamps these DoNotDownload BEFORE starting, so skipped files
    /// never hit disk. Null/empty = download everything (back-compat default).</summary>
    string[]? ExcludedFiles = null);

/// <summary>Body for <c>POST /api/torrents/parse</c>. Echo of the metadata
/// inside a .torrent file (file list + total size) without touching the
/// engine — the Add drawer uses it to preview what's inside the dropped
/// file before the user commits.</summary>
public sealed record ParseTorrentRequest(string Base64Content);

/// <summary>One file slice sent to <c>POST /api/folders/{watcherId}/upload</c>.
/// The HttpAnimarrApiClient packs an array of these into a multipart/form-data
/// request — the Content bytes go straight to disk on the server side.</summary>
public sealed record UploadFilePart(string FileName, byte[] Content);

/// <summary>User-edited fields on the TorrentEdit page.</summary>
public sealed record UpdateTorrentRequest(
    Guid?   FolderWatcherId,
    bool    AutoRename,
    bool    StopAfterDownload,
    int     DownloadLimit,
    int     UploadLimit,
    double? StopSeedingRatio,
    bool    SkipSubfolderStructure,
    bool    SuppressRootFolder,
    string? CustomRootFolderName);

/// <summary>Bulk priority update for the file tree (one item per leaf node).</summary>
public sealed record TorrentFileSelectionUpdate(string FilePath, int Priority);

public sealed record UpdateFileSelectionsRequest(TorrentFileSelectionUpdate[] Selections);
