namespace Animarr.Web.Data.Models;

/// <summary>
/// User-defined collection tag (displayed as a tab on the catalog page).
/// E.g.: "Anime", "Action", "Watching", "Completed".
/// </summary>
public class MediaTag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional CSS color hex: "#6264a7"</summary>
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsAutoTag { get; set; } = false;

    public ICollection<MediaItemTag> Items { get; set; } = [];
}

/// <summary>Many-to-many join between MediaItem and MediaTag.</summary>
public class MediaItemTag
{
    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public Guid MediaTagId { get; set; }
    public MediaTag MediaTag { get; set; } = null!;
}
