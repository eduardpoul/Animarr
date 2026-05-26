namespace Animarr.Shared.Models;

/// <summary>
/// Computed permission flags for the current user (returned by <c>GET /api/me</c>).
/// The client uses these to decide whether to render the Downloads/Admin
/// buttons in TopBar and whether to gate Server-Settings routes.
///
/// Mirrors the four <see cref="RoleDto"/> permission columns 1:1.
/// </summary>
public sealed record PermissionsDto(
    bool ViewContent,
    bool UploadContent,
    bool SystemSettings,
    bool ManageUsers);
