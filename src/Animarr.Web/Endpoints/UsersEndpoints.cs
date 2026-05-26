using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Admin-only CRUD for users + roles. Every endpoint requires the caller to
/// have the <c>manageUsers</c> permission (enforced by the
/// <c>ManageUsers</c> policy). Built-in roles cannot be edited or deleted.
/// </summary>
internal static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Users ───────────────────────────────────────────────────────
        app.MapGet(ApiRoutes.Users, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Users
                .AsNoTracking()
                .Include(u => u.Role)
                .OrderBy(u => u.Name)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(AuthService.ToDto).ToArray());
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapPost(ApiRoutes.Users, async (
            CreateUserRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Name, username and password are required" });
            if (req.Password.Length < 6)
                return Results.BadRequest(new { error = "Password must be at least 6 characters" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == req.RoleId, ct);
            if (role is null) return Results.BadRequest(new { error = "Role not found" });

            var normalised = AuthService.NormaliseUsername(req.Username);
            if (await db.Users.AnyAsync(u => u.Username == normalised, ct))
                return Results.Conflict(new { error = "Username already taken" });

            var user = new User
            {
                Id           = Guid.NewGuid(),
                Name         = req.Name.Trim(),
                Username     = normalised,
                Email        = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
                RoleId       = req.RoleId,
                AvatarHue    = AbsHash(req.Name) % 360,
                PasswordHash = AuthService.HashPassword(req.Password),
                CreatedAt    = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            user.Role = role;
            return Results.Created($"{ApiRoutes.Users}/{user.Id}", AuthService.ToDto(user));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapMethods(ApiRoutes.UserById, new[] { "PATCH" }, async (
            Guid id,
            UpdateUserRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (req.Name is string n)   user.Name = n.Trim();
            if (req.Email is string e)  user.Email = string.IsNullOrWhiteSpace(e) ? null : e.Trim();
            if (req.RoleId is Guid rid)
            {
                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid, ct);
                if (role is null) return Results.BadRequest(new { error = "Role not found" });
                user.RoleId = rid;
                user.Role = role;
            }
            if (!string.IsNullOrEmpty(req.NewPassword))
            {
                if (req.NewPassword.Length < 6)
                    return Results.BadRequest(new { error = "Password must be at least 6 characters" });
                user.PasswordHash = AuthService.HashPassword(req.NewPassword);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(AuthService.ToDto(user));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapDelete(ApiRoutes.UserById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            // Don't allow self-delete — admins must demote/transfer first.
            if (userCtx.CurrentUserId == id)
                return Results.BadRequest(new { error = "You cannot delete your own account while signed in" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            // Don't allow deleting the last Master user — would lock everyone out.
            if (user.Role?.BuiltIn == true && user.Role.Name == "Master")
            {
                var mastersLeft = await db.Users
                    .CountAsync(u => u.RoleId == user.RoleId && u.Id != user.Id, ct);
                if (mastersLeft == 0)
                    return Results.BadRequest(new { error = "Cannot delete the last Master account" });
            }
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        // ─── Roles ───────────────────────────────────────────────────────
        app.MapGet(ApiRoutes.Roles, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
            var counts = await db.Users
                .GroupBy(u => u.RoleId)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.RoleId, g => g.Count, ct);

            return Results.Ok(roles
                .Select(r => AuthService.ToDto(r, counts.GetValueOrDefault(r.Id, 0)))
                .ToArray());
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapPost(ApiRoutes.Roles, async (
            CreateRoleRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Role name is required" });
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Roles.AnyAsync(r => r.Name == req.Name, ct))
                return Results.Conflict(new { error = "Role name already used" });
            var role = new Role
            {
                Id          = Guid.NewGuid(),
                Name        = req.Name.Trim(),
                Description = (req.Description ?? "").Trim(),
                BuiltIn     = false,
                PermViewContent    = req.ViewContent,
                PermUploadContent  = req.UploadContent,
                PermSystemSettings = req.SystemSettings,
                PermManageUsers    = req.ManageUsers,
                FoldersJson        = req.FolderIds is null
                    ? ""
                    : System.Text.Json.JsonSerializer.Serialize(req.FolderIds),
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            return Results.Created($"{ApiRoutes.Roles}/{role.Id}", AuthService.ToDto(role, 0));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapMethods(ApiRoutes.RoleById, new[] { "PATCH" }, async (
            Guid id,
            UpdateRoleRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (role.BuiltIn)
                return Results.Conflict(new { error = "Built-in roles cannot be modified" });
            if (req.Name is string n)        role.Name = n.Trim();
            if (req.Description is string d) role.Description = d.Trim();
            if (req.ViewContent is bool vc)    role.PermViewContent    = vc;
            if (req.UploadContent is bool uc)  role.PermUploadContent  = uc;
            if (req.SystemSettings is bool ss) role.PermSystemSettings = ss;
            if (req.ManageUsers is bool mu)    role.PermManageUsers    = mu;
            if (req.FolderIds is not null)
                role.FoldersJson = req.FolderIds.Length == 0
                    ? ""
                    : System.Text.Json.JsonSerializer.Serialize(req.FolderIds);
            await db.SaveChangesAsync(ct);
            var count = await db.Users.CountAsync(u => u.RoleId == role.Id, ct);
            return Results.Ok(AuthService.ToDto(role, count));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        app.MapDelete(ApiRoutes.RoleById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (role.BuiltIn)
                return Results.Conflict(new { error = "Built-in roles cannot be deleted" });
            if (await db.Users.AnyAsync(u => u.RoleId == role.Id, ct))
                return Results.Conflict(new { error = "Role is in use by one or more users" });
            db.Roles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        return app;
    }

    private static int AbsHash(string s) => Math.Abs(s.GetHashCode());
}
