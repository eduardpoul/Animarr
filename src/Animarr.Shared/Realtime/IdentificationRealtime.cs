using Animarr.Shared.Models;

namespace Animarr.Shared.Realtime;

/// <summary>
/// Server-to-client method names for the identification hub.
/// </summary>
public static class IdentificationHubMethods
{
    /// <summary>Status / progress changed on a queue entry.</summary>
    public const string QueueChanged = "QueueChanged";

    /// <summary>A MediaItem's IdentificationStatus changed (Pending → Identified, etc.).</summary>
    public const string MediaItemChanged = "MediaItemChanged";

    /// <summary>The NeedsReview counter changed — sidebar chip updates.</summary>
    public const string NeedsReviewCountChanged = "NeedsReviewCountChanged";
}

public sealed record IdentificationProgressEnvelope(IdentificationQueueEntryDto Entry);

public sealed record MediaItemStatusEnvelope(Guid MediaItemId, IdentificationStatus Status);

public sealed record NeedsReviewCountEnvelope(int Count);
