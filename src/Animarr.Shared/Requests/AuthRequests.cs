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
