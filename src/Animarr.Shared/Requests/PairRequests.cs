namespace Animarr.Shared.Requests;

/// <summary>POST body for <see cref="ApiRoutes.PairInit"/>. The TV identifies
/// itself with a friendly name so the user can see (in any future "active
/// devices" panel) which TV is asking for access.</summary>
public sealed record PairInitRequest(string DeviceName);

/// <summary>POST body for <see cref="ApiRoutes.PairConfirm"/>. The phone is
/// already authenticated; we read its user from the request cookie, not the
/// body, so a stale form on the phone can't authorise the TV as someone
/// else.</summary>
public sealed record ConfirmPairRequest(string Code);
