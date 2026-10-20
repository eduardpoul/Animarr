namespace Animarr.Web.Data.Models;

/// <summary>
/// A client device (browser profile, phone app install, Android TV) known to
/// this server. The id is server-issued and travels in the HttpOnly
/// <c>animarr_device</c> cookie, sealed with DataProtection (see
/// <c>DeviceTrust</c>), so a client can neither forge nor pick its own.
///
/// The device row outlives the 14-day auth cookie on purpose: it carries the
/// per-device profile list (<see cref="DevicePin"/>) and the "sign in by
/// default" choice, both of which must survive an expired session.
/// </summary>
public class Device
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Bumped on every sign-in / state read from this device.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Sign in by default" profile: when set, opening Animarr on this
    /// device goes straight in as this user — no picker, no PIN. Null = show
    /// the "Who's watching" picker (when ≥2 profiles are known here). Cleared
    /// automatically when the user is deleted or forgets this device.</summary>
    public Guid? DefaultUserId { get; set; }
    public User? DefaultUser { get; set; }
}

/// <summary>
/// One profile known on one device, plus that profile's PIN for THIS device.
/// A row appears the first time the user signs in here (password, setup, TV
/// pairing); <see cref="PinHash"/> stays null until the mandatory "Set a PIN"
/// step completes. Switching to the profile on this device always requires
/// this PIN, verified server-side with a per-row lockout.
/// </summary>
public class DevicePin
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>BCrypt hash of the 4–6 digit PIN, or null while the profile
    /// has signed in here but not set a PIN yet (switching to it then needs a
    /// password sign-in first).</summary>
    public string? PinHash { get; set; }

    /// <summary>Consecutive wrong PINs since the last success. Every 5th
    /// failure starts a cooldown (<see cref="LockedUntil"/>), doubling from
    /// 5 minutes up to an hour.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>While in the future, every PIN check for this row is refused
    /// without even looking at the PIN.</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PinSetAt { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
