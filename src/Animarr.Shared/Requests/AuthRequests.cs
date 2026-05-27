namespace Animarr.Shared.Requests;

/// <summary>POST body for <c>/api/auth/login</c>. Username is case-insensitive
/// (server normalises). Password is plaintext over HTTPS — server hashes
/// with BCrypt before comparison; raw value never persists.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>POST body for <c>/api/auth/setup</c>. Only accepted while the
/// <c>Users</c> table is empty (first-run). Creates the Master account and
/// immediately logs the caller in.</summary>
public sealed record SetupRequest(
    string  Name,
    string  Username,
    string? Email,
    string  Password,
    string  PasswordConfirm);

// ─── PIN (v5 per-user-per-device fast switch) ───────────────────────────────

/// <summary>POST body for <c>/api/me/pin</c>. <see cref="CurrentPassword"/> is
/// re-verified server-side as an anti-CSRF check on top of the auth cookie —
/// matches the change-password contract.</summary>
public sealed record SetPinRequest(string Pin, string CurrentPassword);

/// <summary>DELETE body for <c>/api/me/pin</c>. Clears the user's PIN so future
/// switch-user requests proceed without a keypad. Password challenge is the
/// same anti-CSRF measure as <see cref="SetPinRequest"/>.</summary>
public sealed record ClearPinRequest(string CurrentPassword);

/// <summary>POST body for <c>/api/auth/switch-user</c>. The caller is already
/// signed in as some user; this swaps the cookie to the target user. The
/// server requires <see cref="Pin"/> iff the target user has <c>HasPin=true</c>.
/// Pass null when the target user has no PIN configured.</summary>
public sealed record SwitchUserRequest(Guid UserId, string? Pin);
