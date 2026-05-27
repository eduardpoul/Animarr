namespace Animarr.Shared.Models;

/// <summary>
/// Role definition surfaced to the admin UI (UsersList / RolesList /
/// RoleBuilder). Built-in roles (<c>Master</c>, <c>User</c>) cannot be
/// modified — the client respects <see cref="BuiltIn"/> by disabling the
/// edit/delete affordances.
/// </summary>
public sealed record RoleDto(
    Guid           Id,
    string         Name,
    string         Description,
    bool           BuiltIn,
    PermissionsDto Permissions,
    /// <summary>NULL = "all folders" (default for built-in roles). Non-null array
    /// limits catalog visibility + upload targets to this set of FolderWatcher Ids.</summary>
    Guid[]?        FolderIds,
    int            UserCount);
