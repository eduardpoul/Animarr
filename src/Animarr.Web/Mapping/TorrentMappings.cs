using Animarr.Shared.Models;
using Animarr.Web.Services;
using EntityTorrent = Animarr.Web.Data.Models.TorrentRecord;
using EntityTorrentConfig = Animarr.Web.Data.Models.TorrentConfig;
using EntityFileSelection = Animarr.Web.Data.Models.TorrentFileSelection;

namespace Animarr.Web.Mapping;

internal static class TorrentMappings
{
    public static TorrentRecordDto ToDto(this EntityTorrent entity)
        => new()
        {
            Id              = entity.Id,
            InfoHash        = entity.InfoHash,
            Name            = entity.Name,
            MagnetLink      = entity.MagnetLink,
            TorrentFilePath = entity.TorrentFilePath,
            SavePath        = entity.SavePath,
            FolderWatcherId = entity.FolderWatcherId,
            AddedAt         = entity.AddedAt,
            CompletedAt     = entity.CompletedAt,
            State           = (Animarr.Shared.TorrentRecordState)(int)entity.State,
            TotalSize       = entity.TotalSize,
            Downloaded      = entity.Downloaded,
            Uploaded        = entity.Uploaded,
            DownloadLimit   = entity.DownloadLimit,
            UploadLimit     = entity.UploadLimit,
            StopSeedingRatio = entity.StopSeedingRatio,
            StopAfterDownload = entity.StopAfterDownload,
            SkipSubfolderStructure = entity.SkipSubfolderStructure,
            SuppressRootFolder = entity.SuppressRootFolder,
            CustomRootFolderName = entity.CustomRootFolderName,
            ErrorMessage    = entity.ErrorMessage,
            FileSelections  = entity.FileSelections
                .Select(s => s.ToDto())
                .ToArray(),
        };

    public static TorrentFileSelectionDto ToDto(this EntityFileSelection entity)
        => new(entity.Id, entity.TorrentId, entity.FilePath, entity.Priority, entity.IsDownloaded);

    public static TorrentLiveStatsDto ToDto(this TorrentLiveStats live)
        => new(
            InfoHash: live.InfoHash,
            Name:     live.Name,
            SavePath: live.SavePath,
            State:    live.State.ToString(),
            Progress: live.Progress,
            DownloadRate: live.DownloadRate,
            UploadRate:   live.UploadRate,
            Downloaded:   live.Downloaded,
            Uploaded:     live.Uploaded,
            TotalSize:    live.TotalSize,
            Seeds:        live.Seeds,
            Peers:        live.Peers,
            DownloadLimit: live.DownloadLimit,
            UploadLimit:   live.UploadLimit,
            MetadataReceived: live.MetadataReceived,
            FolderWatcherId:  live.FolderWatcherId);

    public static TorrentFileNodeDto ToDto(this TorrentFileNode node)
        => new()
        {
            Path     = node.Path,
            Name     = node.Name,
            IsDir    = node.IsDir,
            Size     = node.Size,
            Depth    = node.Depth,
            Priority = node.Priority,
            Children = node.Children.Select(c => c.ToDto()).ToList(),
        };

    public static TorrentConfigDto ToDto(this EntityTorrentConfig cfg)
        => new()
        {
            GlobalDownloadLimit = cfg.GlobalDownloadLimit,
            GlobalUploadLimit   = cfg.GlobalUploadLimit,
            ListenPort          = cfg.ListenPort,
            MaxConnections      = cfg.MaxConnections,
            EnableDHT           = cfg.EnableDHT,
            EnableLSD           = cfg.EnableLSD,
            EnableUPnP          = cfg.EnableUPnP,
            StopSeedingAfterDone= cfg.StopSeedingAfterDone,
            StopSeedingRatio    = cfg.StopSeedingRatio,
            DefaultStopAfterDownload = cfg.DefaultStopAfterDownload,
            CacheDirectory      = cfg.CacheDirectory,
        };
}
