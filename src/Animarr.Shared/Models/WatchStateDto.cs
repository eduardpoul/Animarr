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
