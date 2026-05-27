namespace Animarr.Shared.Models;

/// <summary>
/// Account record as exposed by /api/users and /api/me. <b>Never contains the
/// password hash</b> — the server projects users into this DTO before serialising.
/// </summary>
public sealed record UserDto(
    Guid    Id,
    string  Name,
    string  Username,
    string? Email,
    /// <summary>Optional path to a saved avatar image (resolved through /api/image).
    /// NULL → the client renders the initials-disc fallback using <see cref="AvatarHue"/>.</summary>
    string? AvatarPath,
    /// <summary>Deterministic hue 0..359 for the initials-disc fallback.</summary>
    int     AvatarHue,
    Guid    RoleId,
    string  RoleName,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    /// <summary>True iff the user has opted-in to PIN-gated switching from
    /// other devices. Derived server-side from <c>User.PinHash != null</c>;
    /// the keypad-or-not decision in ProfilePanel reads this flag. v5.</summary>
    bool    HasPin = false);
