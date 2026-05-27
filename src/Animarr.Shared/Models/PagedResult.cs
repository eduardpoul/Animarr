namespace Animarr.Shared.Models;

/// <summary>
/// Generic paged response envelope. Used by endpoints that need a total
/// count alongside the page slice (so the client can render the "1–50 of N"
/// pager without a second round-trip).
/// </summary>
public sealed record PagedResult<T>(T[] Items, int Total);
