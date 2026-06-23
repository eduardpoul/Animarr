namespace Animarr.Shared.Models;

/// <summary>
/// Per-file playback progress. Movies use a single row with
/// <see cref="Season"/> / <see cref="Episode"/> = null; series get one row
/// per episode the user has touched.
/// </summary>
public sealed record WatchStateDto
{
    public Guid Id          { get; init; }
    public Guid MediaItemId { get; init; }
    public int? Season      { get; init; }
    public int? Episode     { get; init; }
    public string? FilePath { get; init; }
    public bool  IsWatched  { get; init; }
    public long? ProgressMs { get; init; }
    public long? RuntimeMs  { get; init; }
    public long  TotalWatchTimeSec { get; init; }
    public int   PlayCount  { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime  CreatedAt  { get; init; }
}

/// <summary>
/// Continue-watching hint for MediaDetail's primary CTA. The server resolves
/// "next file to play" given the WatchState rows + season layout; the UI
/// just renders the label and posts back to <see cref="WatchStateDto"/> on
/// playback.
/// </summary>
public sealed record ContinueWatchDto(
    /// <summary>"continue" → resume mid-episode, "next" → start fresh on the next ep,
    /// "first" → nothing watched yet, "rewatch" → everything watched.</summary>
    string Kind,
    string Label,
    Guid MediaItemId,
    int?   Season,
    int?   Episode,
    string? FilePath,
    long?  ProgressMs,
    long?  RuntimeMs);

/// <summary>
/// Continue-watching row enriched with poster + title metadata so the v5
/// Home hero can render 5-slot cards without an extra round-trip per item.
/// One row per (user, mediaItemId) — the latest WatchState in that group.
/// </summary>
public sealed record ContinueWatchItemDto(
    Guid    MediaItemId,
    string  Title,
    string? PosterPath,
    string? FanartPath,
    int?    Year,
    int?    Season,
    int?    Episode,
    long?   ProgressMs,
    long?   RuntimeMs,
    /// <summary>0..1 progress fraction. Convenience so the hero renders the
    /// progress bar without dividing in Razor.</summary>
    double  Progress,
    DateTime LastSeenAt,
    /// <summary>True for "Next Up" rows where the user has finished the run and
    /// a fresh episode landed (the next-up episode is the on-disk finale with
    /// everything before it watched). Drives the hero's "New episode" eyebrow.
    /// Always false for the in-progress continue-watching feed.</summary>
    bool    IsNew = false);
