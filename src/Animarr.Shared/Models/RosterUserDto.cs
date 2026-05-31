namespace Animarr.Shared.Models;

/// <summary>
/// Minimal public profile for the switch-user / "who's watching" picker.
/// EVERY authenticated user may read the roster — that's how a non-admin
/// switches to another profile — so this deliberately OMITS the username and
/// email that <see cref="UserDto"/> carries. Only what the picker renders:
/// display name, role label, avatar, and whether a PIN is required. v5.
/// </summary>
public sealed record RosterUserDto(
    Guid    Id,
    string  Name,
    string  RoleName,
    /// <summary>Deterministic hue 0..359 for the initials-disc fallback.</summary>
    int     AvatarHue,
    /// <summary>Optional saved-avatar path (resolved through /api/image);
    /// NULL → render the initials disc using <see cref="AvatarHue"/>.</summary>
    string? AvatarPath,
    /// <summary>True iff this profile is PIN-gated — the picker shows the
    /// keypad before completing the switch.</summary>
    bool    HasPin);
