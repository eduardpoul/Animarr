namespace Animarr.Shared.Requests;

/// <summary>PUT body for <c>/api/media/{id}/segments</c> — a manual segment
/// override stored with Source=Manual, which detection never clobbers.
/// <see cref="Kind"/> is "intro" | "credits" | "recap". Passing a null
/// <see cref="StartSec"/> or <see cref="EndSec"/> deletes that kind's override
/// (revert to detected / none).</summary>
public sealed record SegmentOverrideRequest(
    int     Season,
    int     Episode,
    string  Kind,
    double? StartSec,
    double? EndSec);
