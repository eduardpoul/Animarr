namespace Animarr.Web.Data.Models;

/// <summary>
/// Per-user "starred" library item. Composite PK (UserId, MediaItemId) so the
/// favorites set can be expressed as a single index lookup.
///
/// Spec: design_handoff_animarr/CHANGELOG_3_FOR_CLAUDE_CODE.md §1
/// — was previously a global Set, now scoped per-user.
/// </summary>
public class UserFavorite
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
