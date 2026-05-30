namespace Animarr.Shared.Models;

/// <summary>
/// Snapshot of the identification-queue processor for the AI status popup.
/// <see cref="IsPaused"/> drives the popup's pause/resume button; the rest mirror
/// the live in-process counters on the server's queue processor.
/// </summary>
public sealed record IdentificationQueueStatusDto(
    bool   IsPaused,
    int    QueueDepth,
    int    ProcessedSinceStart,
    double HitRate);
