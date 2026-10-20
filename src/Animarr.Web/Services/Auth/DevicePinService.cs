using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Per-device profiles, PINs and the "sign in by default" choice — all
/// server-side, keyed by (device id from <see cref="DeviceTrust"/>, user id).
///
/// Rules:
///   • every sign-in on a device (password, setup, TV pairing, PIN switch)
///     registers the profile there; a new profile has no PIN yet and the
///     client must run the blocking "Set a PIN" step;
///   • switching to a profile on a device requires that profile's PIN for the
///     device — there is no PIN-less switch, whatever the roles;
///   • 5 wrong PINs lock the (device, profile) pair for 5 minutes, then 10,
///     20, 40, capped at an hour; a correct PIN resets the counter;
///   • a forgotten PIN is cleared by a password sign-in (or, on a TV, a phone-
///     confirmed pairing) that asks for it, after which a new one is set.
///
/// PIN checks are serialised through one gate: BCrypt costs ~250 ms, a home
/// server sees a handful of them, and it keeps parallel guesses from racing
/// past the failure counter.
/// </summary>
public sealed class DevicePinService
{
    public const int PinMinLength = 4;
    public const int PinMaxLength = 6;
    public const int AttemptsPerWindow = 5;

    private static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxLockout  = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceTrust _deviceTrust;
    private readonly ILogger<DevicePinService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DevicePinService(
        IDbContextFactory<AppDbContext> dbFactory,
        DeviceTrust deviceTrust,
        ILogger<DevicePinService> logger)
    {
        _dbFactory   = dbFactory;
        _deviceTrust = deviceTrust;
        _logger      = logger;
    }

    public enum CheckStatus { Ok, WrongPin, Locked, PinNotSet }

    /// <summary>Outcome of a PIN check against one (device, user) row.</summary>
    public readonly record struct Check(CheckStatus Status, int AttemptsLeft = 0, int RetryAfterSeconds = 0);

    /// <summary>True iff <paramref name="pin"/> is 4–6 ASCII digits.</summary>
    public static bool IsValidPinFormat(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < PinMinLength || pin.Length > PinMaxLength)
            return false;
        foreach (var c in pin)
            if (c < '0' || c > '9') return false;
        return true;
    }

    // ─── Registration ──────────────────────────────────────────────────────

    /// <summary>Record that <paramref name="userId"/> signed in on the calling
    /// device: creates the device (and its cookie) if needed, adds the profile
    /// row if new, bumps last-used. Optionally clears the profile's PIN here
    /// (the "Forgot PIN" path after a password / pairing sign-in).</summary>
    public async Task RegisterSignInAsync(HttpContext http, Guid userId, bool resetPin = false,
        CancellationToken ct = default)
    {
        try
        {
            var deviceId = await _deviceTrust.EnsureDeviceIdAsync(http, ct);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTime.UtcNow;
            var row = await db.DevicePins.FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == userId, ct);
            if (row is null)
            {
                db.DevicePins.Add(new DevicePin
                {
                    DeviceId = deviceId, UserId = userId,
                    CreatedAt = now, LastUsedAt = now,
                });
            }
            else
            {
                row.LastUsedAt = now;
                if (resetPin)
                {
                    row.PinHash        = null;
                    row.PinSetAt       = null;
                    row.FailedAttempts = 0;
                    row.LockedUntil    = null;
                }
            }
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
            if (device is not null)
            {
                device.LastSeenAt = now;
                // A profile whose PIN was just reset can't stay the silent
                // default — the default always points at a PIN-protected profile.
                if (resetPin && device.DefaultUserId == userId) device.DefaultUserId = null;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Never fail a sign-in over bookkeeping; the client's next device
            // read simply won't list the profile.
            _logger.LogWarning(ex, "Device registration failed for {UserId}", userId);
        }
    }

    // ─── State ─────────────────────────────────────────────────────────────

    /// <summary>The calling device's profile state for the picker / gates.</summary>
    public async Task<DeviceStateDto> GetStateAsync(HttpContext http, Guid? currentUserId, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null)
            return new DeviceStateDto(false, currentUserId, false, null,
                Array.Empty<DeviceProfileDto>(), PinMinLength, PinMaxLength);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        var rows = await db.DevicePins
            .AsNoTracking()
            .Include(p => p.User!).ThenInclude(u => u.Role)
            .Where(p => p.DeviceId == deviceId)
            .ToListAsync(ct);

        var defaultId = device?.DefaultUserId;
        var profiles = rows
            .Where(r => r.User is not null)
            .OrderByDescending(r => r.LastUsedAt)
            .Select(r => new DeviceProfileDto(
                r.UserId,
                r.User!.Name,
                r.User.Role?.Name ?? "",
                r.User.AvatarHue,
                r.User.AvatarPath,
                HasPin:    !string.IsNullOrEmpty(r.PinHash),
                IsDefault: r.UserId == defaultId,
                IsCurrent: r.UserId == currentUserId))
            .ToArray();

        var currentHasPin = currentUserId is Guid cu
            && rows.Any(r => r.UserId == cu && !string.IsNullOrEmpty(r.PinHash));

        return new DeviceStateDto(true, currentUserId, currentHasPin, defaultId, profiles,
            PinMinLength, PinMaxLength);
    }

    /// <summary>True iff <paramref name="userId"/> has a PIN on the calling device.</summary>
    public async Task<bool> HasPinAsync(HttpContext http, Guid userId, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return false;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DevicePins.AnyAsync(p => p.DeviceId == deviceId && p.UserId == userId && p.PinHash != null, ct);
    }

    /// <summary>Profile ids of the calling device that have a PIN set — the
    /// roster uses it to flag which profiles are switchable here.</summary>
    public async Task<HashSet<Guid>> GetPinnedUserIdsAsync(HttpContext http, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return new();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await db.DevicePins
            .Where(p => p.DeviceId == deviceId && p.PinHash != null)
            .Select(p => p.UserId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    // ─── PIN checks ────────────────────────────────────────────────────────

    /// <summary>Verify <paramref name="pin"/> for <paramref name="userId"/> on
    /// the calling device, applying the lockout. Unknown device / profile / no
    /// PIN set → <see cref="CheckStatus.PinNotSet"/> (no counter to bump).</summary>
    public async Task<Check> VerifyAsync(HttpContext http, Guid userId, string pin, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return new Check(CheckStatus.PinNotSet);

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.DevicePins.FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == userId, ct);
            if (row is null || string.IsNullOrEmpty(row.PinHash)) return new Check(CheckStatus.PinNotSet);
            return await CheckRowAsync(db, row, pin, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Set or change the PIN of <paramref name="userId"/> on the
    /// calling device. With no PIN yet, <paramref name="currentPin"/> is not
    /// needed (first PIN after a sign-in); otherwise it must verify, and wrong
    /// guesses count toward the same lockout as switching.</summary>
    public async Task<Check> SetPinAsync(HttpContext http, Guid userId, string newPin, string? currentPin,
        CancellationToken ct = default)
    {
        if (!IsValidPinFormat(newPin)) throw new ArgumentException("PIN must be 4–6 digits.", nameof(newPin));

        // The caller is signed in as userId, so the device must exist — but a
        // session cookie copied off another device may lack one; register then.
        var deviceId = await _deviceTrust.EnsureDeviceIdAsync(http, ct);

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTime.UtcNow;
            var row = await db.DevicePins.FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == userId, ct);
            if (row is null)
            {
                row = new DevicePin { DeviceId = deviceId, UserId = userId, CreatedAt = now };
                db.DevicePins.Add(row);
            }
            else if (!string.IsNullOrEmpty(row.PinHash))
            {
                var check = await CheckRowAsync(db, row, currentPin ?? "", ct);
                if (check.Status != CheckStatus.Ok) return check;
            }

            row.PinHash        = AuthService.HashPin(newPin);
            row.PinSetAt       = now;
            row.LastUsedAt     = now;
            row.FailedAttempts = 0;
            row.LockedUntil    = null;
            await db.SaveChangesAsync(ct);
            return new Check(CheckStatus.Ok);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Shared lockout + verify on a tracked row. Saves the counter.</summary>
    private static async Task<Check> CheckRowAsync(AppDbContext db, DevicePin row, string pin, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (row.LockedUntil is DateTime until && until > now)
            return new Check(CheckStatus.Locked, 0, (int)Math.Ceiling((until - now).TotalSeconds));

        if (IsValidPinFormat(pin) && AuthService.VerifyPin(pin, row.PinHash!))
        {
            row.FailedAttempts = 0;
            row.LockedUntil    = null;
            row.LastUsedAt     = now;
            await db.SaveChangesAsync(ct);
            return new Check(CheckStatus.Ok);
        }

        row.FailedAttempts++;
        var inWindow = row.FailedAttempts % AttemptsPerWindow;
        if (inWindow == 0)
        {
            // 5 → 5 min, 10 → 10 min, 15 → 20 min … capped at an hour.
            var windows = row.FailedAttempts / AttemptsPerWindow;
            var minutes = BaseLockout.TotalMinutes * Math.Pow(2, Math.Min(windows - 1, 8));
            var lockout = TimeSpan.FromMinutes(Math.Min(minutes, MaxLockout.TotalMinutes));
            row.LockedUntil = now + lockout;
            await db.SaveChangesAsync(ct);
            return new Check(CheckStatus.Locked, 0, (int)Math.Ceiling(lockout.TotalSeconds));
        }
        await db.SaveChangesAsync(ct);
        return new Check(CheckStatus.WrongPin, AttemptsPerWindow - inWindow);
    }

    // ─── Forget / default ──────────────────────────────────────────────────

    /// <summary>Remove <paramref name="userId"/>'s profile (and PIN) from the
    /// calling device — sign-out. Clears the device default if it was them.</summary>
    public async Task ForgetAsync(HttpContext http, Guid userId, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DevicePins.FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == userId, ct);
        if (row is not null) db.DevicePins.Remove(row);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is not null && device.DefaultUserId == userId) device.DefaultUserId = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The calling device's "sign in by default" profile, or null.
    /// Ignores a default whose PIN has since gone (it can't be silent then).</summary>
    public async Task<Guid?> GetDefaultUserIdAsync(HttpContext http, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var defaultId = await db.Devices
            .Where(d => d.Id == deviceId)
            .Select(d => d.DefaultUserId)
            .FirstOrDefaultAsync(ct);
        if (defaultId is null) return null;
        var pinned = await db.DevicePins.AnyAsync(
            p => p.DeviceId == deviceId && p.UserId == defaultId && p.PinHash != null, ct);
        return pinned ? defaultId : null;
    }

    public enum DefaultResult { Ok, NoDevice, PinRequired }

    /// <summary>Make <paramref name="userId"/> the calling device's default
    /// (they must have a PIN here, so the default is always a completed
    /// profile), or clear the default when null.</summary>
    public async Task<DefaultResult> SetDefaultAsync(HttpContext http, Guid? userId, CancellationToken ct = default)
    {
        var deviceId = await _deviceTrust.GetDeviceIdAsync(http, ct);
        if (deviceId is null) return userId is null ? DefaultResult.Ok : DefaultResult.NoDevice;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return DefaultResult.NoDevice;
        if (userId is Guid uid)
        {
            var pinned = await db.DevicePins.AnyAsync(
                p => p.DeviceId == deviceId && p.UserId == uid && p.PinHash != null, ct);
            if (!pinned) return DefaultResult.PinRequired;
        }
        device.DefaultUserId = userId;
        device.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return DefaultResult.Ok;
    }
}
