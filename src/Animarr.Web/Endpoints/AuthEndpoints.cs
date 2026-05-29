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
            if (req.Theme is string th)                   prefs.Theme              = ValidateTheme(th);
            if (req.HeroPagerStyle is string hps)         prefs.HeroPagerStyle     = ValidatePagerStyle(hps);
            prefs.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(prefs));
        }).RequireAuthorization();

        // ─── Edit own profile (Name / Email / AvatarHue) ───────────────
        // Self-edit surface that doesn't require the ManageUsers permission
        // (which gates /api/users/{id}). The signed-in user can patch their
        // own display fields; password changes still go through /me/password
        // with a current-password challenge, and RoleId is admin-only.
        // When Name changes we re-issue the cookie so the topbar avatar chip
        // / sidebar / DLNA "controlled by" labels pick up the new value on
        // the next request without forcing a re-login.
        app.MapMethods(ApiRoutes.MeProfile, new[] { "PATCH" }, async (
            UpdateMyProfileRequest req,
            AuthService auth,
            IUserContext userCtx,
            HttpContext http,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var u = await db.Users.Include(x => x.Role).FirstOrDefaultAsync(x => x.Id == uid, ct);
            if (u is null) return Results.Unauthorized();

            var nameChanged = false;
            if (req.Name is string n)
            {
                var trimmed = n.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    return Results.BadRequest(new { error = "Name cannot be empty" });
                if (!string.Equals(trimmed, u.Name, StringComparison.Ordinal))
                {
                    u.Name = trimmed;
                    nameChanged = true;
                }
            }
            if (req.Email is string e)
                u.Email = string.IsNullOrWhiteSpace(e) ? null : e.Trim();
            if (req.AvatarHue is int hue)
                u.AvatarHue = ((hue % 360) + 360) % 360; // normalise into [0, 359]

            await db.SaveChangesAsync(ct);

            // Re-issue cookie when the display name changed so the topbar /
            // sidebar avatar chip refresh without forcing the user to log out.
            // Cookie carries Name as a claim, so without this the chip would
            // keep showing the old value until next /api/me round-trip.
            if (nameChanged)
            {
                try
                {
                    await auth.SignOutCookieAsync(http);
                    await auth.SignInCookieAsync(http, u);
                }
                catch { /* cookie re-issue is opportunistic — UserCtx.RefreshMeAsync still picks it up */ }
            }
            return Results.Ok(AuthService.ToDto(u));
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

        // ─── PIN (v5 per-user-per-device fast switch) ──────────────────
        //
        // POST   /api/me/pin             — set or change the PIN
        // DELETE /api/me/pin             — clear the PIN
        // POST   /api/auth/switch-user   — swap cookie to another user (PIN-gated)
        //
        // Anti-CSRF: both /api/me/pin mutations re-verify the password just like
        // /api/me/password. The auth cookie alone is not sufficient — without
        // re-verification a stolen cookie could pin-lock the account.

        app.MapPost(ApiRoutes.MePin, async (
            SetPinRequest req,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            try { AuthService.ValidatePinFormat(req.Pin); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
            if (u is null) return Results.Unauthorized();
            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(req.CurrentPassword, u.PasswordHash); }
            catch { ok = false; }
            if (!ok) return Results.BadRequest(new { error = "Current password is incorrect" });

            u.PinHash = AuthService.HashPin(req.Pin);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapMethods(ApiRoutes.MePin, new[] { "DELETE" }, async (
            [Microsoft.AspNetCore.Mvc.FromBody] ClearPinRequest req,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
            if (u is null) return Results.Unauthorized();
            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(req.CurrentPassword, u.PasswordHash); }
            catch { ok = false; }
            if (!ok) return Results.BadRequest(new { error = "Current password is incorrect" });

            u.PinHash = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Switch-user. The caller must already be signed in; we sign them out
        // of the current cookie, then sign in as the target. The PIN check
        // (when PinHash is set on the target) is the gate that keeps a guest
        // who borrows a signed-in laptop from hopping into the Master account
        // just by clicking. No PIN configured → any signed-in user can switch
        // to that target (typical for the Master "no PIN" setup on a trusted
        // home server).
        app.MapPost(ApiRoutes.AuthSwitchUser, async (
            SwitchUserRequest req,
            AuthService auth,
            IUserContext userCtx,
            HttpContext http,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (userCtx.CurrentUserId is null) return Results.Unauthorized();
            // No-op when target == current. Don't bother re-issuing the cookie.
            if (userCtx.CurrentUserId == req.UserId)
            {
                await using var db1 = await dbFactory.CreateDbContextAsync(ct);
                var self = await db1.Users
                    .Include(u => u.Role)
                    .FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
                return self is null ? Results.Unauthorized() : Results.Ok(AuthService.ToDto(self));
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var target = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
            if (target is null) return Results.NotFound(new { error = "User not found" });

            // PIN check — only when the target opted in. Use Unauthorized
            // (401) so the keypad UI can shake + clear and let the user retry.
            if (!string.IsNullOrEmpty(target.PinHash))
            {
                if (string.IsNullOrEmpty(req.Pin) || !AuthService.VerifyPin(req.Pin, target.PinHash))
                    return Results.Json(new { error = "Invalid PIN" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Reset LastSeenAt opportunistically so the strip's "last active"
            // hint stays useful across switches.
            try
            {
                target.LastSeenAt = DateTime.UtcNow;
                db.Entry(target).Property(u => u.LastSeenAt).IsModified = true;
                await db.SaveChangesAsync(ct);
            }
            catch { }

            await auth.SignOutCookieAsync(http);
            await auth.SignInCookieAsync(http, target);
            return Results.Ok(AuthService.ToDto(target));
        }).RequireAuthorization();

        // ─── Per-user favorites (v5) ─────────────────────────────────────
        // POST /api/me/favorites/{id} → star title for current user (idempotent)
        // DELETE /api/me/favorites/{id} → unstar (no-op if absent)
        // GET /api/me/favorites → array of starred media-item IDs
        app.MapPost("/api/me/favorites/{mediaItemId:guid}", async (
            Guid mediaItemId,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // Verify the media item exists so we don't accumulate orphaned
            // favorites pointing at deleted catalog rows.
            var exists = await db.MediaItems.AnyAsync(m => m.Id == mediaItemId, ct);
            if (!exists) return Results.NotFound();
            var fav = await db.UserFavorites
                .FirstOrDefaultAsync(f => f.UserId == uid && f.MediaItemId == mediaItemId, ct);
            if (fav is null)
            {
                db.UserFavorites.Add(new Animarr.Web.Data.Models.UserFavorite
                {
                    UserId      = uid.Value,
                    MediaItemId = mediaItemId,
                    CreatedAt   = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapDelete("/api/me/favorites/{mediaItemId:guid}", async (
            Guid mediaItemId,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var fav = await db.UserFavorites
                .FirstOrDefaultAsync(f => f.UserId == uid && f.MediaItemId == mediaItemId, ct);
            if (fav is not null)
            {
                db.UserFavorites.Remove(fav);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet(ApiRoutes.MeFavorites, async (
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = await db.UserFavorites
                .Where(f => f.UserId == uid)
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => f.MediaItemId)
                .ToArrayAsync(ct);
            return Results.Ok(ids);
        }).RequireAuthorization();

        // ─── Continue Watching (v5) ──────────────────────────────────────
        // GET /api/me/continue?take=N → at most N most-recent in-progress
        // titles for the current user. "In progress" means there's at least
        // one WatchState row for that media item where:
        //   • progress > 5% (so an accidental tap doesn't bury the list)
        //   • progress < 95% (already-finished episodes drop off)
        //   • IsWatched = false (toggled-watched titles drop off)
        // The newest row per media item wins so a series doesn't appear
        // 12 times in the hero.
        app.MapGet(ApiRoutes.MeContinue, async (
            int? take,
            IUserContext userCtx,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            var limit = Math.Clamp(take ?? 8, 1, 24);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // EF can't ORDER+GROUP cleanly here, so we do the dedup in memory
            // after a single pass. Small dataset (the user's recent plays) —
            // O(N) post-processing is fine.
            var rows = await db.WatchStates
                .AsNoTracking()
                .Where(w => w.UserId == uid && !w.IsWatched && w.ProgressMs.HasValue && w.RuntimeMs.HasValue && w.RuntimeMs > 0)
                .OrderByDescending(w => w.LastSeenAt)
                .Take(200) // safety cap
                .ToListAsync(ct);

            // Dedup by MediaItemId, keep newest LastSeenAt.
            var byItem = rows
                .GroupBy(w => w.MediaItemId)
                .Select(g => g.OrderByDescending(w => w.LastSeenAt).First())
                .Where(w =>
                {
                    var pct = (double)w.ProgressMs!.Value / w.RuntimeMs!.Value;
                    return pct >= 0.05 && pct < 0.95;
                })
                .Take(limit)
                .ToList();

            var mediaIds = byItem.Select(w => w.MediaItemId).ToList();
            var titles = await db.MediaItems
                .AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id))
                .Select(m => new {
                    m.Id, m.Title, m.PosterPath, m.FanartPath, m.Year,
                })
                .ToListAsync(ct);
            var titleMap = titles.ToDictionary(t => t.Id);

            var result = byItem
                .Where(w => titleMap.ContainsKey(w.MediaItemId))
                .Select(w =>
                {
                    var t = titleMap[w.MediaItemId];
                    var pct = (double)w.ProgressMs!.Value / w.RuntimeMs!.Value;
                    return new ContinueWatchItemDto(
                        MediaItemId: w.MediaItemId,
                        Title:       t.Title,
                        PosterPath:  t.PosterPath,
                        FanartPath:  t.FanartPath,
                        Year:        t.Year,
                        Season:      w.Season,
                        Episode:     w.Episode,
                        ProgressMs:  w.ProgressMs,
                        RuntimeMs:   w.RuntimeMs,
                        Progress:    Math.Clamp(pct, 0, 1),
                        LastSeenAt:  w.LastSeenAt ?? DateTime.UtcNow);
                })
                .ToArray();

            return Results.Ok(result);
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
        p.Language,
        p.Theme,
        p.HeroPagerStyle);

    // Whitelist of valid theme slugs — must match the [data-theme] keys in
    // Styles/themes/*.css. Unknown slugs fall back to "quietude" so a stale
    // client can't write a value that breaks the cascade.
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "quietude", "cinematic", "terminal", "anime",
        "ember-edge", "mountain-sect", "immortal-path",
        "heavens-defiance", "ink-and-paper", "cyberpunk",
    };
    private static string ValidateTheme(string t)
        => ValidThemes.Contains(t) ? t.ToLowerInvariant() : "quietude";

    private static readonly HashSet<string> ValidPagerStyles = new(StringComparer.OrdinalIgnoreCase) { "f", "g", "h" };
    private static string ValidatePagerStyle(string s)
        => ValidPagerStyles.Contains(s) ? s.ToLowerInvariant() : "g";
}
