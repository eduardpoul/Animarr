using Animarr.Web.Data.Models;

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
        // EF Core translates IReadOnlyList<Guid>.Contains to SQL IN (...).
        return source.Where(m => allowed.Contains(m.FolderId));
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
