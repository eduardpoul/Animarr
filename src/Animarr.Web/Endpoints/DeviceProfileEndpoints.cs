using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Per-device profiles: the "Who's watching" picker, mandatory per-device PINs
/// and the "Sign in by default" choice. All PIN verification happens here, on
/// the server, against <see cref="DevicePinService"/>.
///
///   GET  /api/auth/device               — device state (anonymous)
///   POST /api/auth/switch-user          — sign in as a profile by its PIN (anonymous-callable)
///   POST /api/auth/device/pin           — set / change my PIN on this device
///   PUT  /api/auth/device/default       — make me the device default / clear it
///   POST /api/auth/device/enter-default — open as the device default, no PIN (anonymous)
///
/// The anonymous ones are scoped by the sealed device cookie, not a session:
/// without a device cookie there are no profiles, no PINs to guess and no
/// default to enter.
/// </summary>
internal static class DeviceProfileEndpoints
{
    public static IEndpointRouteBuilder MapDeviceProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.AuthDevice, async (
            DevicePinService pins,
            IUserContext userCtx,
            HttpContext http,
            CancellationToken ct) =>
            Results.Ok(await pins.GetStateAsync(http, userCtx.CurrentUserId, ct)))
            .AllowAnonymous();

        // Sign in on this device as a profile, by that profile's PIN for this
        // device. No session needed (the picker after the auth cookie expired);
        // with one, it swaps the cookie. Picking the CURRENT profile verifies
        // the PIN too — the startup picker must not wave the last user through.
        app.MapPost(ApiRoutes.AuthSwitchUser, async (
            SwitchUserRequest req,
            AuthService auth,
            DevicePinService pins,
            HttpContext http,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var target = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
            if (target is null) return Results.NotFound(new { error = "not_found" });

            // A missing / malformed PIN (e.g. an older client that still sends
            // null) is rejected before it can touch the attempt counter.
            if (!DevicePinService.IsValidPinFormat(req.Pin))
            {
                return await pins.HasPinAsync(http, target.Id, ct)
                    ? Results.BadRequest(new { error = PinErrorCodes.Invalid, message = "PIN must be 4–6 digits." })
                    : PinNotSet();
            }

            var check = await pins.VerifyAsync(http, target.Id, req.Pin!, ct);
            if (check.Status != DevicePinService.CheckStatus.Ok) return PinFailure(http, check);

            try
            {
                target.LastSeenAt = DateTime.UtcNow;
                db.Entry(target).Property(u => u.LastSeenAt).IsModified = true;
                await db.SaveChangesAsync(ct);
            }
            catch { /* opportunistic */ }

            await auth.SignInCookieAsync(http, target);
            return Results.Ok(AuthService.ToDto(target));
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Pin);

        // Set (first time on this device — the mandatory step after a sign-in,
        // or after "Forgot PIN") or change (proves the current PIN) my PIN.
        app.MapPost(ApiRoutes.AuthDevicePin, async (
            SetDevicePinRequest req,
            DevicePinService pins,
            IUserContext userCtx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (userCtx.CurrentUserId is not Guid uid) return Results.Unauthorized();
            if (!DevicePinService.IsValidPinFormat(req.Pin))
                return Results.BadRequest(new { error = PinErrorCodes.Invalid, message = "PIN must be 4–6 digits." });

            var check = await pins.SetPinAsync(http, uid, req.Pin, req.CurrentPin, ct);
            return check.Status == DevicePinService.CheckStatus.Ok
                ? Results.NoContent()
                : PinFailure(http, check);
        }).RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Pin);

        // Enabled → the signed-in user becomes this device's default (opens
        // straight in, no picker, no PIN). Disabled → clear the default; any
        // profile signed in here may do that, since it only adds a PIN prompt.
        app.MapPut(ApiRoutes.AuthDeviceDefault, async (
            SetDeviceDefaultRequest req,
            DevicePinService pins,
            IUserContext userCtx,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (userCtx.CurrentUserId is not Guid uid) return Results.Unauthorized();
            var result = await pins.SetDefaultAsync(http, req.Enabled ? uid : null, ct);
            return result switch
            {
                DevicePinService.DefaultResult.Ok => Results.Ok(await pins.GetStateAsync(http, uid, ct)),
                DevicePinService.DefaultResult.PinRequired => Results.Conflict(new
                {
                    error = PinErrorCodes.PinNotSet,
                    message = "Set a PIN on this device first.",
                }),
                _ => Results.Conflict(new { error = "no_device", message = "This device isn't registered." }),
            };
        }).RequireAuthorization();

        // Open as the device's default profile. The default was chosen by that
        // profile itself (after its PIN) and means exactly "this device goes
        // straight in as me", so no PIN here. Works across an expired session.
        app.MapPost(ApiRoutes.AuthDeviceEnterDefault, async (
            AuthService auth,
            DevicePinService pins,
            HttpContext http,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var defaultId = await pins.GetDefaultUserIdAsync(http, ct);
            if (defaultId is null) return Results.NotFound(new { error = "no_default" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == defaultId, ct);
            if (user is null) return Results.NotFound(new { error = "no_default" });

            try
            {
                user.LastSeenAt = DateTime.UtcNow;
                db.Entry(user).Property(u => u.LastSeenAt).IsModified = true;
                await db.SaveChangesAsync(ct);
            }
            catch { /* opportunistic */ }

            await auth.SignInCookieAsync(http, user);
            return Results.Ok(AuthService.ToDto(user));
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Pin);

        return app;
    }

    private static IResult PinNotSet() => Results.Json(new
    {
        error = PinErrorCodes.PinNotSet,
        message = "This profile has no PIN on this device yet. Sign in with its password.",
    }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult PinFailure(HttpContext http, DevicePinService.Check check)
    {
        switch (check.Status)
        {
            case DevicePinService.CheckStatus.WrongPin:
                return Results.Json(new
                {
                    error = PinErrorCodes.WrongPin,
                    message = "Wrong PIN.",
                    attemptsLeft = check.AttemptsLeft,
                }, statusCode: StatusCodes.Status401Unauthorized);
            case DevicePinService.CheckStatus.Locked:
                http.Response.Headers.RetryAfter = check.RetryAfterSeconds.ToString();
                return Results.Json(new
                {
                    error = PinErrorCodes.Locked,
                    message = "Too many wrong PINs. Try again later.",
                    retryAfterSeconds = check.RetryAfterSeconds,
                }, statusCode: StatusCodes.Status429TooManyRequests);
            default:
                return PinNotSet();
        }
    }
}
