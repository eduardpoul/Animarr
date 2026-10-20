namespace Animarr.Shared.Models;

/// <summary>
/// The calling device's profile state — <c>GET /api/auth/device</c>. Readable
/// anonymously (the device cookie, not the auth cookie, identifies the
/// device) so the "Who's watching" picker still works after the 14-day
/// session cookie expired. A request without a device cookie gets an empty
/// state (<see cref="Registered"/> = false, no profiles).
/// </summary>
public sealed record DeviceStateDto(
    /// <summary>True iff the request carried a valid server-issued device id.</summary>
    bool Registered,
    /// <summary>User of the current session, or null when anonymous.</summary>
    Guid? CurrentUserId,
    /// <summary>True iff the current session's user has a PIN on THIS device.
    /// False while signed in = the client must show the blocking "Set a PIN"
    /// step before anything else. Always false when anonymous.</summary>
    bool CurrentUserHasPin,
    /// <summary>"Sign in by default" profile for this device, or null.</summary>
    Guid? DefaultUserId,
    /// <summary>Profiles that have signed in on this device, most recently
    /// used first.</summary>
    DeviceProfileDto[] Profiles,
    /// <summary>Accepted PIN length range (inclusive).</summary>
    int PinMinLength = 4,
    int PinMaxLength = 6);

/// <summary>One profile known on the calling device. Same privacy rules as
/// <see cref="RosterUserDto"/>: no username, no email.</summary>
public sealed record DeviceProfileDto(
    Guid    Id,
    string  Name,
    string  RoleName,
    int     AvatarHue,
    string? AvatarPath,
    /// <summary>True once the profile set its PIN on this device. False =
    /// it can't be switched to with a PIN yet — sign in with the password.</summary>
    bool    HasPin,
    /// <summary>True iff this is the device's "Sign in by default" profile.</summary>
    bool    IsDefault,
    /// <summary>True iff this profile owns the current session.</summary>
    bool    IsCurrent);

/// <summary>Outcome of a server-side PIN check (switch / sign-in by PIN, or a
/// PIN change that proves the current PIN). Built client-side from the
/// endpoint's status code + error body so callers never need try/catch for
/// the expected failures.</summary>
public sealed record PinAuthResult(
    PinAuthStatus Status,
    /// <summary>The signed-in user on <see cref="PinAuthStatus.Ok"/> from a
    /// switch; null for PIN changes.</summary>
    UserDto? User = null,
    /// <summary>Tries left before the cooldown, on <see cref="PinAuthStatus.WrongPin"/>.</summary>
    int? AttemptsLeft = null,
    /// <summary>Seconds until PIN entry unlocks, on <see cref="PinAuthStatus.Locked"/>.</summary>
    int? RetryAfterSeconds = null)
{
    public bool Ok => Status == PinAuthStatus.Ok;
}

public enum PinAuthStatus
{
    Ok,
    /// <summary>Wrong PIN — see <see cref="PinAuthResult.AttemptsLeft"/>.</summary>
    WrongPin,
    /// <summary>Too many wrong PINs — cooldown running.</summary>
    Locked,
    /// <summary>The profile has no PIN on this device (or isn't known here):
    /// sign in with its password instead.</summary>
    PinNotSet,
    /// <summary>Target user doesn't exist.</summary>
    NotFound,
    /// <summary>Malformed PIN (not 4–6 digits).</summary>
    Invalid,
    /// <summary>Anything else (transport, 5xx).</summary>
    Failed,
}

/// <summary>Wire error codes carried as <c>{ "error": "…" }</c> by the PIN
/// endpoints, next to an optional <c>attemptsLeft</c> / <c>retryAfterSeconds</c>.</summary>
public static class PinErrorCodes
{
    public const string WrongPin  = "wrong_pin";
    public const string Locked    = "locked";
    public const string PinNotSet = "pin_not_set";
    public const string Invalid   = "invalid_pin";
}
