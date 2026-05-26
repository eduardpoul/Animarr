namespace Animarr.Shared.Models;

/// <summary>
/// Composite "who am I + what can I do + what folders can I see" payload
/// returned by <c>GET /api/me</c>. The TopBar reads this once on boot to
/// avoid extra round-trips for permission checks.
/// </summary>
public sealed record MeDto(
    UserDto        User,
    PermissionsDto Permissions,
    /// <summary>NULL = "all folders" — view everything. Non-null = restricted set;
    /// the catalog filter intersects MediaItem.FolderId with this array.</summary>
    Guid[]?        AllowedFolderIds);

/// <summary>
/// Probe payload for the bootstrap router. Lets the UI decide between
/// /welcome (auth flow), /login (returning user), and /setup (first run).
/// Free to call anonymously.
/// </summary>
public sealed record AuthStatusDto(
    /// <summary>True iff the <c>Users</c> table is empty — first-run wizard required.</summary>
    bool SetupRequired,
    /// <summary>True iff the current request carries a valid auth cookie.</summary>
    bool Authenticated);
