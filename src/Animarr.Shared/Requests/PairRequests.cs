namespace Animarr.Shared.Requests;

/// <summary>POST body for <see cref="ApiRoutes.PairInit"/>. The TV identifies
/// itself with a friendly name so the user can see (in any future "active
/// devices" panel) which TV is asking for access. <see cref="ResetPin"/> is the
/// TV's "Forgot PIN" path: once a phone confirms, the confirming user's PIN on
/// this TV is cleared so the TV asks for a new one (pairing stands in for the
/// password sign-in a TV remote can't type). Older TV builds omit it.</summary>
public sealed record PairInitRequest(string DeviceName, bool ResetPin = false);

/// <summary>POST body for <see cref="ApiRoutes.PairConfirm"/>. The phone is
/// already authenticated; we read its user from the request cookie, not the
/// body, so a stale form on the phone can't authorise the TV as someone
/// else.</summary>
public sealed record ConfirmPairRequest(string Code);
