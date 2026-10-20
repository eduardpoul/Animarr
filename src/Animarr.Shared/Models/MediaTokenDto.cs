namespace Animarr.Shared.Models;

/// <summary>
/// The caller's session media token (GET /api/media-token). Clients that can't
/// attach the auth cookie to media requests append it as <c>?mt=</c> to byte
/// URLs (/api/image, /api/video, /api/file, /api/subtitle, …) and pass it as
/// the SignalR access token. Bound to <see cref="UserId"/>; the same value is
/// returned for every request inside a 6-hour window, valid until
/// <see cref="ExpiresAtUtc"/> (at most 12 h).
/// </summary>
public sealed record MediaTokenDto(string Token, DateTimeOffset ExpiresAtUtc, Guid UserId);
