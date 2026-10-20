using Microsoft.AspNetCore.DataProtection;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Server-side memory of which profiles have signed in on THIS device — the
/// gate behind <c>/api/auth/switch-user</c>.
///
/// Profile switching swaps the auth cookie without a password, so the server
/// must decide which targets are fair game. The client-side device PIN is UX,
/// not security: any signed-in user can call the endpoint directly. The rule:
///
///   • switching to a profile whose role grants nothing the caller's role
///     lacks (a "downward" or sideways switch) is always allowed;
///   • switching to a MORE privileged profile is allowed only when that
///     profile has itself signed in on this device before (password login,
///     setup, TV pairing confirmed from that account, or an earlier switch).
///
/// The record is an HttpOnly cookie carrying a DataProtection-sealed list of
/// user ids — unforgeable without the server's key ring, so a kid's phone that
/// Master never used can't be talked into a Master session.
/// </summary>
public sealed class DeviceTrust
{
    public const string CookieName = "animarr_device";

    /// <summary>Cap on remembered profiles — keeps the cookie small; the oldest
    /// entries drop off first.</summary>
    private const int MaxProfiles = 16;

    private const string ItemsKey = "animarr.deviceTrust";

    private readonly IDataProtector _protector;

    public DeviceTrust(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector("Animarr.DeviceTrust.v1");
    }

    /// <summary>Profiles remembered for the device that sent this request
    /// (including any added earlier in the same request).</summary>
    public IReadOnlyList<Guid> Read(HttpContext http)
    {
        if (http.Items[ItemsKey] is List<Guid> pending) return pending;

        var raw = http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return Array.Empty<Guid>();
        try
        {
            return _protector.Unprotect(raw)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch
        {
            // Tampered, or sealed by a key ring we no longer have.
            return Array.Empty<Guid>();
        }
    }

    public bool IsRemembered(HttpContext http, Guid userId) => Read(http).Contains(userId);

    /// <summary>Add <paramref name="userId"/> to this device's profiles. Safe to
    /// call several times per request — the cookie is written once, with the
    /// merged list, right before the response starts.</summary>
    public void Remember(HttpContext http, Guid userId)
    {
        var list = new List<Guid>(Read(http));
        list.Remove(userId);
        list.Add(userId);   // most recent last
        if (list.Count > MaxProfiles) list.RemoveRange(0, list.Count - MaxProfiles);

        var firstCall = http.Items[ItemsKey] is null;
        http.Items[ItemsKey] = list;
        if (!firstCall) return;

        http.Response.OnStarting(() =>
        {
            var final = (List<Guid>)http.Items[ItemsKey]!;
            http.Response.Cookies.Append(CookieName, _protector.Protect(string.Join(',', final)),
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
            return Task.CompletedTask;
        });
    }
}
