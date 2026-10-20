using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// IQueryable extension that intersects a <c>MediaItem</c> stream with the
/// current user's role-allowed folder set. Pass <see cref="Role"/> from the
/// hydrated user; if the role has no allowlist (built-in or
/// <c>FoldersJson</c> empty) the query passes through unchanged.
///
/// Usage:
/// <code>
///   var me   = await userContext.GetCurrentUserAsync();
///   var rows = await db.MediaItems
///       .ApplyRoleFolderFilter(me?.Role)
///       .ToListAsync();
/// </code>
/// </summary>
public static class RoleFolderFilter
{
    public static IQueryable<MediaItem> ApplyRoleFolderFilter(
        this IQueryable<MediaItem> source, Role? role)
    {
        if (role is null) return source;
        var allowed = role.GetAllowedFolderIds();
        if (allowed is null || allowed.Count == 0)
            return source; // "all folders" — no filter
        // The role editor lists SECTION folders (GetSectionFolders), while a
        // MediaItem hangs off its own title folder inside a section — so match
        // either the item's folder or that folder's parent section. EF Core
        // translates IReadOnlyList<Guid>.Contains to SQL IN (...).
        return source.Where(m => allowed.Contains(m.FolderId)
            || (m.Folder != null && m.Folder.ParentSectionId != null
                && allowed.Contains(m.Folder.ParentSectionId.Value)));
    }

    /// <summary>Ids of every MediaItem the role may see, or <c>null</c> when the
    /// role is unrestricted. For post-filtering results that are assembled in
    /// memory (recommendation / franchise rails).</summary>
    public static async Task<HashSet<Guid>?> VisibleIdsAsync(
        this IQueryable<MediaItem> source, Role? role, CancellationToken ct = default)
    {
        if (role?.GetAllowedFolderIds() is null) return null;
        return (await source.ApplyRoleFolderFilter(role).Select(m => m.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Whether the role may see the MediaItem with this id — the
    /// by-id endpoints answer 404 otherwise, as if it didn't exist.</summary>
    public static async Task<bool> IsVisibleAsync(
        this IQueryable<MediaItem> source, Role? role, Guid id, CancellationToken ct = default)
        => role?.GetAllowedFolderIds() is null
           || await source.ApplyRoleFolderFilter(role).AnyAsync(m => m.Id == id, ct);

    /// <summary>Folder-level counterpart for FolderWatcher listings: a section
    /// on the allowlist, or a title folder inside one.</summary>
    public static bool AllowsFolder(this Role? role, FolderWatcher folder)
    {
        var allowed = role?.GetAllowedFolderIds();
        return allowed is null
            || allowed.Contains(folder.Id)
            || (folder.ParentSectionId is Guid parent && allowed.Contains(parent));
    }

    /// <summary>Generic helper for tables that carry a FolderId column —
    /// used by RenameHistory, TorrentRecord etc. when we surface per-user views.</summary>
    public static IQueryable<T> ApplyRoleFolderFilter<T>(
        this IQueryable<T> source,
        Role? role,
        System.Linq.Expressions.Expression<Func<T, Guid>> folderIdSelector)
    {
        if (role is null) return source;
        var allowed = role.GetAllowedFolderIds();
        if (allowed is null || allowed.Count == 0)
            return source;

        var parameter = folderIdSelector.Parameters[0];
        var body = System.Linq.Expressions.Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            new[] { typeof(Guid) },
            System.Linq.Expressions.Expression.Constant(allowed),
            folderIdSelector.Body);
        var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter);
        return source.Where(lambda);
    }
}
