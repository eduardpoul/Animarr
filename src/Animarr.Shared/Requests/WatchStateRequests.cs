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
