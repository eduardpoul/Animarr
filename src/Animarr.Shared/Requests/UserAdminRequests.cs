namespace Animarr.Shared.Requests;

// ─── User CRUD ─────────────────────────────────────────────────────────────

/// <summary>POST body for <c>/api/users</c>. Admin-only (manageUsers).</summary>
public sealed record CreateUserRequest(
    string  Name,
    string  Username,
    string? Email,
    Guid    RoleId,
    string  Password);

/// <summary>PATCH body for <c>/api/users/{id}</c>. Every field is optional —
/// the server applies whichever ones are non-null. Set
/// <see cref="NewPassword"/> to a non-empty string to reset the password
/// (admin-driven; no current-password challenge).</summary>
public sealed record UpdateUserRequest(
    string? Name,
    string? Email,
    Guid?   RoleId,
    string? NewPassword);

// ─── Role CRUD ─────────────────────────────────────────────────────────────

/// <summary>POST body for <c>/api/roles</c>. <see cref="FolderIds"/> NULL =
/// "all folders" (the built-in <c>User</c> default). Empty array = "no
/// folders" (rare, intentionally rendered as a chip in admin).</summary>
public sealed record CreateRoleRequest(
    string Name,
    string Description,
    bool   ViewContent,
    bool   UploadContent,
    bool   SystemSettings,
    bool   ManageUsers,
    Guid[]? FolderIds);

/// <summary>PATCH body for <c>/api/roles/{id}</c>. Built-in roles reject this
/// with 409 Conflict regardless of payload.</summary>
public sealed record UpdateRoleRequest(
    string? Name,
    string? Description,
    bool?   ViewContent,
    bool?   UploadContent,
    bool?   SystemSettings,
    bool?   ManageUsers,
    Guid[]? FolderIds);
