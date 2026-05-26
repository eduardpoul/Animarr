namespace Animarr.Web.Data.Models;

/// <summary>
/// A named bundle of permissions + folder-scope. Two roles are <see cref="BuiltIn"/>
/// and cannot be edited or deleted: <c>Master</c> (all four perms + all folders)
/// and <c>User</c> (viewContent only + all folders). Custom roles can mix any
/// permission set with any folder allowlist.
///
/// Spec: design_handoff_animarr/CHANGELOG_3_FOR_CLAUDE_CODE.md §1 + §6.
/// </summary>
public class Role
{
    public Guid Id { get; set; }

    /// <summary>Display name — also doubles as the role chip label in admin UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>System-managed roles (<c>Master</c>, <c>User</c>). Built-in roles
    /// cannot be deleted or have their permissions edited; only their description
    /// is mutable in case localisation tweaks are needed.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Free-text shown in the Roles list to explain what the role does.</summary>
    public string Description { get; set; } = "";

    // ─── Permission flags ────────────────────────────────────────────────────
    // Stored as plain bool columns rather than a JSON bag so EF queries can
    // filter on them cheaply (`WHERE PermSystemSettings = 1`). The four perms
    // are intentionally the minimum that captures the v4 design's role matrix.

    /// <summary>Playback, marking watched, favoriting. Default for all roles.</summary>
    public bool PermViewContent { get; set; } = true;

    /// <summary>Add downloads — magnets, .torrent files, file uploads.</summary>
    public bool PermUploadContent { get; set; }

    /// <summary>Access Server Settings (root folders, LLM, patterns, etc.).</summary>
    public bool PermSystemSettings { get; set; }

    /// <summary>CRUD users + roles. Separate from <see cref="PermSystemSettings"/>
    /// so a "Family admin" can manage accounts without touching LLM/patterns.</summary>
    public bool PermManageUsers { get; set; }

    /// <summary>Folder scope. Empty string = "all folders" (built-in default).
    /// Non-empty = JSON array of <see cref="FolderWatcher.Id"/> values the role
    /// is restricted to. The catalog visibility filter intersects this set with
    /// every <c>/api/media*</c> query. Stored as a string because SQLite has no
    /// native array type — JSON keeps the schema portable to Postgres later.</summary>
    public string FoldersJson { get; set; } = "";

    /// <summary>Convenience: returns <c>null</c> for "all folders" or the parsed
    /// GUID array. Helper for <c>RoleFolderFilter</c>.</summary>
    public IReadOnlyList<Guid>? GetAllowedFolderIds()
    {
        if (string.IsNullOrEmpty(FoldersJson)) return null;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(FoldersJson);
            return arr is null || arr.Length == 0 ? null : arr;
        }
        catch
        {
            return null;
        }
    }
}
