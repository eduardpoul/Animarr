namespace Animarr.Shared.Requests;

/// <summary>Magnet URI + destination folder. The engine resolves metadata on its own.</summary>
public sealed record AddMagnetRequest(
    string MagnetLink,
    Guid?  FolderWatcherId,
    bool   AutoRename,
    bool   StopAfterDownload,
    bool   SkipSubfolderStructure,
    bool   SuppressRootFolder,
    string? CustomRootFolderName);

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
    string? CustomRootFolderName);

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
