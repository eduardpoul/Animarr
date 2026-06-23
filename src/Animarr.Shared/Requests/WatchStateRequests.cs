namespace Animarr.Shared.Requests;

/// <summary>Progress ping from the player — partial updates accepted.</summary>
public sealed record RecordProgressRequest(
    Guid    MediaItemId,
    int?    Season,
    int?    Episode,
    string? FilePath,
    long    ProgressMs,
    long?   RuntimeMs);

/// <summary>Explicit user action — "mark watched" / "mark unwatched".</summary>
public sealed record ToggleWatchedRequest(
    Guid    MediaItemId,
    int?    Season,
    int?    Episode,
    string? FilePath,
    bool    IsWatched);

/// <summary>"Reset progress" CTA — drops the resume offset back to zero
/// without flipping IsWatched.</summary>
public sealed record ResetProgressRequest(
    Guid    MediaItemId,
    int?    Season,
    int?    Episode,
    string? FilePath);

/// <summary>One (season, episode) coordinate for bulk watch operations.
/// Movies would use null/null, but bulk is series-only in practice.</summary>
public sealed record EpisodeRef(int? Season, int? Episode);

/// <summary>"Mark previous episodes watched" — the retroactive bulk toggle the
/// MediaDetail popup fires when the user marks an episode watched while earlier
/// on-disk episodes are still unwatched. Upserts one WatchState row per entry,
/// all set to <see cref="IsWatched"/>. Server returns the affected rows so the
/// client can patch its in-memory list without a re-fetch.</summary>
public sealed record MarkBulkWatchedRequest(
    Guid         MediaItemId,
    EpisodeRef[] Episodes,
    bool         IsWatched);
