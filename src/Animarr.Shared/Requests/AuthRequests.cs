namespace Animarr.Shared.Requests;

/// <summary>POST body for <c>/api/auth/login</c>. Username is case-insensitive
/// (server normalises). Password is plaintext over HTTPS — server hashes
/// with BCrypt before comparison; raw value never persists.
/// <see cref="ResetDevicePin"/> is the "Forgot PIN" path: after the password
/// verifies, the user's PIN on THIS device is cleared so the client's
/// mandatory "Set a PIN" step asks for a new one. Older clients omit it.</summary>
public sealed record LoginRequest(string Username, string Password, bool ResetDevicePin = false);

/// <summary>POST body for <c>/api/auth/setup</c>. Only accepted while the
/// <c>Users</c> table is empty (first-run). Creates the Master account and
/// immediately logs the caller in.</summary>
public sealed record SetupRequest(
    string  Name,
    string  Username,
    string? Email,
    string  Password,
    string  PasswordConfirm);

// ─── Per-device PIN + profile picker ───────────────────────────────────────

/// <summary>POST body for <c>/api/auth/device/pin</c> — set or change the
/// signed-in user's PIN on THIS device (4–6 digits). <see cref="CurrentPin"/>
/// is required only when a PIN already exists here (a change); the first PIN
/// after a sign-in needs none. A forgotten PIN is reset by signing in with the
/// password (<see cref="LoginRequest.ResetDevicePin"/>), not here.</summary>
public sealed record SetDevicePinRequest(string Pin, string? CurrentPin = null);

/// <summary>PUT body for <c>/api/auth/device/default</c>. <c>true</c> makes the
/// signed-in user this device's "Sign in by default" profile (they must have a
/// PIN here); <c>false</c> clears the device default, whoever it was.</summary>
public sealed record SetDeviceDefaultRequest(bool Enabled);

/// <summary>POST body for <c>/api/auth/switch-user</c>: sign in on this device
/// as <see cref="UserId"/> by that profile's PIN for this device. Works with
/// or without a current session (the "Who's watching" picker after the auth
/// cookie expired). The PIN is always required and always verified on the
/// server; 5 wrong tries start a cooldown.</summary>
public sealed record SwitchUserRequest(Guid UserId, string? Pin);
