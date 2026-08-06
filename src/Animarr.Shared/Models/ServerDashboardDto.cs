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

    /// <summary>Sum of MediaItem.EpisodeCount (denormalised, metadata-sourced)
    /// across the library. NOT a live file count — episode-to-file mapping is
    /// resolved on the fly (MediaFileResolver) and never persisted per file, so
    /// there is no cheap DB-only way to count on-disk episodes; walking the
    /// filesystem every minute was ruled out on cost. This is the closest cheap
    /// proxy for "how big is the library".</summary>
    int EpisodesTotal,

    /// <summary>TorrentRecords currently in the Downloading state.</summary>
    int TorrentsDownloading,

    /// <summary>TorrentRecords currently in the Seeding state.</summary>
    int TorrentsSeeding,

    /// <summary>Sum of TotalSize across active downloads.</summary>
    long DownloadingBytesTotal,

    /// <summary>Sum of Downloaded across active downloads.</summary>
    long DownloadingBytesDone);
