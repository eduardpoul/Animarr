namespace Animarr.Shared.Models;

/// <summary>
/// Aggregate-only summary for external dashboards (e.g. Homepage's
/// <c>customapi</c> widget, polled every minute). Anonymous-accessible — flat
/// numbers, no titles, no users, no paths.
/// </summary>
public sealed record ServerDashboardDto(
    /// <summary>Total MediaItems in the library.</summary>
    int Titles,

    /// <summary>MediaItems whose CreatedAt falls within the last 7 days.</summary>
    int TitlesAddedWeek,

    /// <summary>Episodes with an on-disk file mapping.</summary>
    int EpisodesOnDisk,

    /// <summary>TorrentRecords currently in the Downloading state.</summary>
    int TorrentsDownloading,

    /// <summary>TorrentRecords currently in the Seeding state.</summary>
    int TorrentsSeeding,

    /// <summary>Sum of TotalSize across active downloads.</summary>
    long DownloadingBytesTotal,

    /// <summary>Sum of Downloaded across active downloads.</summary>
    long DownloadingBytesDone);
