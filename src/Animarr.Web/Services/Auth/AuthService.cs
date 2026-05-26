using System.Security.Claims;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Cookie-session auth + first-run setup + role/user CRUD building blocks.
///
/// Password hashing: BCrypt work factor 12 (~250ms on a modern x86 core). The
/// raw password parameter is held in scope for the minimum time required to
/// hash/verify, never logged, never persisted.
///
/// Username normalisation: usernames are stored lowercased so case differences
/// don't accidentally let two people register "Alice" and "alice". Login also
/// lowercases the incoming username before lookup.
/// </summary>
public sealed class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AuthService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public static string NormaliseUsername(string username) =>
        (username ?? "").Trim().ToLowerInvariant();

    /// <summary>True iff the <c>Users</c> table is empty — first-run wizard required.</summary>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return !await db.Users.AnyAsync(ct);
    }

    /// <summary>Seed the two built-in roles if they don't exist yet. Idempotent —
    /// safe to call on every app startup.</summary>
    public async Task EnsureBuiltInRolesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Roles.Where(r => r.BuiltIn).ToListAsync(ct);

        if (!existing.Any(r => r.Name == "Master"))
        {
            db.Roles.Add(new Role
            {
                Id          = Guid.NewGuid(),
                Name        = "Master",
                BuiltIn     = true,
                Description = "Full access. Cannot be edited or deleted.",
                PermViewContent    = true,
                PermUploadContent  = true,
                PermSystemSettings = true,
                PermManageUsers    = true,
                FoldersJson        = "", // empty = "all folders"
            });
        }
        if (!existing.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new Role
            {
                Id          = Guid.NewGuid(),
                Name        = "User",
                BuiltIn     = true,
                Description = "View-only — playback, marking watched, favoriting.",
                PermViewContent    = true,
                PermUploadContent  = false,
                PermSystemSettings = false,
                PermManageUsers    = false,
                FoldersJson        = "",
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Look up the built-in Master role. Throws if seeding hasn't run yet.</summary>
    public async Task<Role> GetMasterRoleAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var master = await db.Roles.FirstOrDefaultAsync(r => r.BuiltIn && r.Name == "Master", ct);
        return master ?? throw new InvalidOperationException("Built-in Master role missing — call EnsureBuiltInRolesAsync first.");
    }

    /// <summary>Look up the built-in User role. Throws if seeding hasn't run yet.</summary>
    public async Task<Role> GetUserRoleAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.BuiltIn && r.Name == "User", ct);
        return role ?? throw new InvalidOperationException("Built-in User role missing — call EnsureBuiltInRolesAsync first.");
    }

    /// <summary>Create the first Master account. Rejects if Users isn't empty
    /// (the only way to get a second Master is via the admin Users page).</summary>
    public async Task<(bool ok, string? error, User? user)> CreateInitialMasterAsync(
        string name, string username, string? email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(username))
            return (false, "Name and username are required", null);
        if (password is null || password.Length < 6)
            return (false, "Password must be at least 6 characters", null);

        await EnsureBuiltInRolesAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(ct))
            return (false, "Setup is not allowed — at least one user already exists", null);

        var master = await db.Roles.FirstAsync(r => r.BuiltIn && r.Name == "Master", ct);
        var user = new User
        {
            Id           = Guid.NewGuid(),
            Name         = name.Trim(),
            Username     = NormaliseUsername(username),
            Email        = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            RoleId       = master.Id,
            AvatarHue    = StableHueFromName(name),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            CreatedAt    = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Setup: created initial Master user {Username}", user.Username);
        return (true, null, user);
    }

    /// <summary>Verify credentials. Returns the hydrated user (with Role) on
    /// success or null. Does NOT issue the cookie — callers do that via
    /// <see cref="SignInCookieAsync"/>.</summary>
    public async Task<User?> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var normalised = NormaliseUsername(username);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == normalised, ct);
        if (user is null) return null;

        bool ok;
        try { ok = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash); }
        catch { ok = false; }
        if (!ok) return null;

        // Bump LastSeenAt opportunistically — no error if it doesn't land.
        try
        {
            user.LastSeenAt = DateTime.UtcNow;
            db.Entry(user).Property(u => u.LastSeenAt).IsModified = true;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LastSeenAt update failed for {UserId}", user.Id);
        }
        return user;
    }

    /// <summary>Issue the auth cookie for a verified user. Persistent across
    /// browser restarts (14-day expiry) — the cookie is the session, no
    /// server-side store needed.</summary>
    public Task SignInCookieAsync(HttpContext http, User user)
    {
        var claims = new[]
        {
            new Claim(AuthConstants.UserIdClaim, user.Id.ToString()),
            new Claim(AuthConstants.RoleIdClaim, user.RoleId.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
        };
        var identity = new ClaimsIdentity(claims, AuthConstants.CookieScheme);
        var principal = new ClaimsPrincipal(identity);
        return http.SignInAsync(AuthConstants.CookieScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(14),
            });
    }

    public Task SignOutCookieAsync(HttpContext http) =>
        http.SignOutAsync(AuthConstants.CookieScheme);

    /// <summary>Hash a password for storage (used by user CRUD).</summary>
    public static string HashPassword(string raw) =>
        BCrypt.Net.BCrypt.HashPassword(raw, workFactor: 12);

    /// <summary>Map User → UserDto. Centralised so password hash never leaks.</summary>
    public static UserDto ToDto(User u) => new(
        u.Id,
        u.Name,
        u.Username,
        u.Email,
        u.AvatarPath,
        u.AvatarHue,
        u.RoleId,
        u.Role?.Name ?? "",
        u.CreatedAt,
        u.LastSeenAt);

    /// <summary>Map Role → RoleDto.</summary>
    public static RoleDto ToDto(Role r, int userCount) => new(
        r.Id,
        r.Name,
        r.Description,
        r.BuiltIn,
        new PermissionsDto(
            r.PermViewContent,
            r.PermUploadContent,
            r.PermSystemSettings,
            r.PermManageUsers),
        r.GetAllowedFolderIds()?.ToArray(),
        userCount);

    /// <summary>FNV-1a hash mod 360. Stable for the same name string across
    /// machines and processes — used so an Avatar's initials-disc colour
    /// stays put as the user renames.</summary>
    private static int StableHueFromName(string name)
    {
        const uint offset = 2166136261u;
        const uint prime  = 16777619u;
        uint h = offset;
        foreach (var c in name)
        {
            h ^= c;
            h *= prime;
        }
        return (int)(h % 360u);
    }
}
