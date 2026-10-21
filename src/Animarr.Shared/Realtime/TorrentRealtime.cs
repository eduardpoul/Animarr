using Animarr.Shared.Models;

namespace Animarr.Shared.Realtime;

/// <summary>
/// Methods the torrent hub sends to clients. The server pushes a snapshot when
/// a torrent's stats change (at most ~1 Hz), every ~15 s as a resync while
/// torrents exist, and once to each new connection.
/// </summary>
public static class TorrentHubMethods
{
    /// <summary>Wholesale snapshot — current set of all live torrents.</summary>
    public const string LiveSnapshot = "LiveSnapshot";

    /// <summary>Persisted-row mutation (state transition, completion, deletion).</summary>
    public const string RecordChanged = "RecordChanged";

    /// <summary>The persisted row was deleted server-side (rebalance/cleanup).</summary>
    public const string RecordRemoved = "RecordRemoved";
}

public sealed record TorrentSnapshotEnvelope(TorrentLiveStatsDto[] Items, DateTime At);

public sealed record TorrentRecordRemovedEnvelope(Guid Id);
