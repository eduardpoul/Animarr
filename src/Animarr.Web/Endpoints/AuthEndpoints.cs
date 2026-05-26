using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Auth flow + per-user identity surface: login/logout/setup,
/// <c>GET /api/me</c>, <c>GET|PATCH /api/me/preferences</c>, password change.
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Status probe (anonymous) ────────────────────────────────────
        // Bootstrap router calls this once on first load: drives /welcome
        // vs /login vs /setup vs /catalog decision.
        app.MapGet(ApiRoutes.AuthStatus, async (
            AuthService auth,
            IUserContext user,
            CancellationToken ct) =>
        {
            var setupRequired = await auth.IsSetupRequiredAsync(ct);
            return Results.Ok(new AuthStatusDto(
                SetupRequired: setupRequired,
                Authenticated: user.IsAuthenticated));
        }).AllowAnonymous();

        // ─── First-run Setup ─────────────────────────────────────────────
        app.MapPost(ApiRoutes.AuthSetup, async (
            SetupRequest req,
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (req.Password != req.PasswordConfirm)
                return Results.BadRequest(new { error = "Passwords do not match" });

            var (ok, error, user) = await auth.CreateInitialMasterAsync(
                req.Name, req.Username, req.Email, req.Password, ct);
            if (!ok || user is null)
                return Results.BadRequest(new { error = error ?? "Setup failed" });

            // Pre-load role for the cookie claim
            await using var db = await http.RequestServices
                .GetRequiredService<IDbContextFactory<AppDbContext>>()
                .CreateDbContextAsync(ct);
            user.Role = await db.Roles.FirstAsync(r => r.Id == user.RoleId, ct);

            await auth.SignInCookieAsync(http, user);
            return Results.Ok(AuthService.ToDto(user));
        }).AllowAnonymous();

        // ─── Login / Logout ──────────────────────────────────────────────
        app.MapPost(ApiRoutes.AuthLogin, async (
            LoginRequest req,
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var user = await auth.VerifyPasswordAsync(req.Username, req.Password, ct);
            if (user is null)
                return Results.Unauthorized();
            await auth.SignInCookieAsync(http, user);
            return Results.Ok(AuthService.ToDto(user));
        }).AllowAnonymous();

        app.MapPost(ApiRoutes.AuthLogout, async (
            AuthService auth,
            HttpContext http) =>
        {
            await auth.SignOutCookieAsync(http);
            return Results.NoContent();
        }).AllowAnonymous();

        // ─── Me ──────────────────────────────────────────────────────────
        app.MapGet(ApiRoutes.Me, async (
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var u = await userCtx.GetCurrentUserAsync(ct);
            if (u is null || u.Role is null) return Results.Unauthorized();

            return Results.Ok(new MeDto(
                User: AuthService.ToDto(u),
                Permissions: new PermissionsDto(
                    u.Role.PermViewContent,
                    u.Role.PermUploadContent,
                    u.Role.PermSystemSettings,
                    u.Role.PermManageUsers),
                AllowedFolderIds: u.Role.GetAllowedFolderIds()?.ToArray()));
        }).RequireAuthorization();

        // ─── Preferences ─────────────────────────────────────────────────
        app.MapGet(ApiRoutes.MePreferences, async (
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid, ct);
            // Lazy-create defaults on first read.
            if (prefs is null)
            {
                prefs = new UserPreferences { UserId = uid.Value };
                db.UserPreferences.Add(prefs);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(prefs));
        }).RequireAuthorization();

        app.MapMethods(ApiRoutes.MePreferences, new[] { "PATCH" }, async (
            UpdatePreferencesRequest req,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid, ct);
            if (prefs is null)
            {
                prefs = new UserPreferences { UserId = uid.Value };
                db.UserPreferences.Add(prefs);
            }

            if (req.AccentHue is int hue)                 prefs.AccentHue          = Math.Clamp(hue, 0, 359);
            if (req.BackdropEnabled is bool bge)          prefs.BackdropEnabled    = bge;
            if (req.BackdropBlurPx is int blur)           prefs.BackdropBlurPx     = Math.Clamp(blur, 0, 30);
            if (req.BackdropBrightness is int br)         prefs.BackdropBrightness = Math.Clamp(br, 10, 80);
            if (req.BackdropIntervalSec is int interval)  prefs.BackdropIntervalSec= Math.Clamp(interval, 5, 600);
            if (req.TvMode is bool tv)                    prefs.TvMode             = tv;
            if (req.AudioPreferredLanguage is string apl) prefs.AudioPreferredLanguage = apl;
            if (req.SubtitlePreferredLanguage is string spl) prefs.SubtitlePreferredLanguage = spl;
            if (req.SubtitleSize is int ss)               prefs.SubtitleSize       = Math.Clamp(ss, 10, 48);
            if (req.DefaultVolume is int vol)             prefs.DefaultVolume      = Math.Clamp(vol, 0, 100);
            if (req.AudioPassthrough is bool ap)          prefs.AudioPassthrough   = ap;
            if (req.NormalizeVolume is bool nv)           prefs.NormalizeVolume    = nv;
            if (req.Language is string lang)              prefs.Language           = lang;
            prefs.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(prefs));
        }).RequireAuthorization();

        // ─── Change own password ────────────────────────────────────────
        app.MapPost(ApiRoutes.MePassword, async (
            ChangePasswordRequest req,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 6)
                return Results.BadRequest(new { error = "New password must be at least 6 characters" });
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
            if (u is null) return Results.Unauthorized();
            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(req.CurrentPassword, u.PasswordHash); }
            catch { ok = false; }
            if (!ok) return Results.BadRequest(new { error = "Current password is incorrect" });
            u.PasswordHash = AuthService.HashPassword(req.NewPassword);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static UserPreferencesDto ToDto(UserPreferences p) => new(
        p.AccentHue,
        p.BackdropEnabled,
        p.BackdropBlurPx,
        p.BackdropBrightness,
        p.BackdropIntervalSec,
        p.TvMode,
        p.AudioPreferredLanguage,
        p.SubtitlePreferredLanguage,
        p.SubtitleSize,
        p.DefaultVolume,
        p.AudioPassthrough,
        p.NormalizeVolume,
        p.Language);
}
