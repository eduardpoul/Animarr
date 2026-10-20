using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Server-issued device identity — the key the per-device profile list, PINs
/// and "sign in by default" choice hang off (<see cref="DevicePinService"/>).
///
/// The id lives in the HttpOnly <c>animarr_device</c> cookie, sealed with
/// DataProtection: unforgeable without the server's key ring, so a client can't
/// claim another device's id (and with it that device's PIN rows). It is
/// independent of the 14-day auth cookie and slides for ~400 days (the browser
/// cap), refreshed on every sign-in, so the device keeps its profiles across
/// session expiry. The MAUI shells persist it with the rest of their cookie jar.
///
/// Legacy (v1) cookies carried a sealed list of user ids instead of a device
/// id. They are upgraded on first sight: a Device row is created, each listed
/// user becomes a known profile WITHOUT a PIN (a client-side PIN from the old
/// scheme can't be trusted server-side), and a v2 cookie replaces the old one.
/// </summary>
public sealed class DeviceTrust
{
    public const string CookieName = "animarr_device";

    private const string ItemsKey      = "animarr.deviceId";
    private const string WrittenKey    = "animarr.deviceCookieWritten";
    private const string PayloadPrefix = "d:";

    private readonly IDataProtector _protector;
    private readonly IDataProtector _legacyProtector;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DeviceTrust> _logger;

    public DeviceTrust(
        IDataProtectionProvider dataProtection,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<DeviceTrust> logger)
    {
        _protector       = dataProtection.CreateProtector("Animarr.DeviceTrust.v2");
        _legacyProtector = dataProtection.CreateProtector("Animarr.DeviceTrust.v1");
        _dbFactory       = dbFactory;
        _logger          = logger;
    }

    /// <summary>The calling device's id, or null when the request carries no
    /// (valid) device cookie. Never creates a device for a cookie-less caller —
    /// anonymous visitors don't get rows; <see cref="EnsureDeviceIdAsync"/>
    /// does that on sign-in. Cached per request.</summary>
    public async Task<Guid?> GetDeviceIdAsync(HttpContext http, CancellationToken ct = default)
    {
        if (http.Items[ItemsKey] is Guid cached) return cached;

        var raw = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return null;

        if (TryUnprotect(_protector, raw, out var payload)
            && payload.StartsWith(PayloadPrefix, StringComparison.Ordinal)
            && Guid.TryParse(payload.AsSpan(PayloadPrefix.Length), out var id))
        {
            // The id was sealed by our own key ring, so it is ours even if the
            // row is gone (DB restored from an older backup) — re-adopt it.
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (!await db.Devices.AnyAsync(d => d.Id == id, ct))
            {
                db.Devices.Add(new Device { Id = id });
                await db.SaveChangesAsync(ct);
            }
            http.Items[ItemsKey] = id;
            return id;
        }

        if (TryUnprotect(_legacyProtector, raw, out var legacy))
        {
            var userIds = legacy
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
            var newId = await CreateDeviceAsync(userIds, ct);
            _logger.LogInformation("DeviceTrust: upgraded a v1 device cookie ({Count} profiles) to device {DeviceId}",
                userIds.Count, newId);
            http.Items[ItemsKey] = newId;
            WriteCookie(http, newId);
            return newId;
        }

        // Tampered, or sealed by a key ring we no longer have.
        return null;
    }

    /// <summary>The calling device's id, creating a device (and issuing its
    /// cookie) when the request has none. Also slides the cookie's expiry.
    /// Call from sign-in paths only.</summary>
    public async Task<Guid> EnsureDeviceIdAsync(HttpContext http, CancellationToken ct = default)
    {
        var existing = await GetDeviceIdAsync(http, ct);
        if (existing is Guid id)
        {
            WriteCookie(http, id);
            return id;
        }

        var created = await CreateDeviceAsync(Array.Empty<Guid>(), ct);
        http.Items[ItemsKey] = created;
        WriteCookie(http, created);
        return created;
    }

    private async Task<Guid> CreateDeviceAsync(IReadOnlyCollection<Guid> knownUserIds, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var device = new Device { Id = Guid.NewGuid() };
        db.Devices.Add(device);
        if (knownUserIds.Count > 0)
        {
            var existing = await db.Users
                .Where(u => knownUserIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var uid in existing)
                db.DevicePins.Add(new DevicePin
                {
                    DeviceId = device.Id, UserId = uid, PinHash = null,
                    CreatedAt = now, LastUsedAt = now,
                });
        }
        await db.SaveChangesAsync(ct);
        return device.Id;
    }

    /// <summary>(Re)issue the device cookie — once per request.</summary>
    private void WriteCookie(HttpContext http, Guid deviceId)
    {
        if (http.Items.ContainsKey(WrittenKey) || http.Response.HasStarted) return;
        http.Items[WrittenKey] = true;
        http.Response.Cookies.Append(CookieName, _protector.Protect(PayloadPrefix + deviceId.ToString("N")),
            new CookieOptions
            {
                HttpOnly    = true,
                SameSite    = SameSiteMode.Lax,
                Secure      = http.Request.IsHttps,
                IsEssential = true,
                Path        = "/",
                // Browsers cap cookie lifetime at ~400 days anyway.
                Expires     = DateTimeOffset.UtcNow.AddDays(400),
            });
    }

    private static bool TryUnprotect(IDataProtector protector, string raw, out string payload)
    {
        try
        {
            payload = protector.Unprotect(raw);
            return true;
        }
        catch
        {
            payload = "";
            return false;
        }
    }
}
