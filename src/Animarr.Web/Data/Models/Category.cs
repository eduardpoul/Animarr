namespace Animarr.Web.Data.Models;

/// <summary>
/// Higher-level catalog categorisation tag — e.g. "Movies", "Serials", "Anime",
/// "Donghua", "Dorama", "Multi", "Kids". Coarser than TMDB genres
/// (Action/Drama/Romance); a MediaItem can belong to zero or more categories.
///
/// Seven categories ship as <see cref="BuiltIn"/>=true via CategorySeedService
/// and cannot be deleted by admins (only renamed/disabled — except built-ins
/// can NOT be renamed). Custom categories can be added freely.
///
/// Classification is driven by <c>CategoryClassifierService</c> — typically
/// via the LLM (using <see cref="Hint"/> as a prompt aid), with a deterministic
/// heuristic fallback when the LLM is disabled or unreachable.
/// </summary>
public class Category
{
    public Guid Id { get; set; }

    /// <summary>Display name — also the chip label on Home and the unique key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>System-managed categories. Built-ins can be disabled or have
    /// description/hint/sortOrder/enabled edited, but never deleted and never renamed.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Admins can toggle a category off without losing it. Disabled
    /// categories are skipped by the classifier and hidden from the chip strip.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Short human-readable explanation shown in the Server Settings
    /// admin row beside the chip.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Prompt aid passed to the LLM during classification — examples:
    /// "Korean TV dramas and Korean drama films, NOT Korean action/horror".
    /// Should describe the inclusion/exclusion rules in plain English.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>Ordering for the chip strip on Home. Lower comes first.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaItemCategory> Items { get; set; } = [];
}

/// <summary>
/// Many-to-many join between <see cref="MediaItem"/> and <see cref="Category"/>.
///
/// <see cref="Source"/> tracks WHERE the assignment came from so the classifier
/// can refresh LLM/heuristic picks without trampling user-applied overrides:
///   • "manual"    — user edited categories on the item; SACRED, never overwritten
///   • "llm"       — written by the LLM classifier
///   • "heuristic" — written by the deterministic fallback
///   • "seed"      — initial seeding (rare; reserved)
/// </summary>
public class MediaItemCategory
{
    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    /// <summary>"llm" | "manual" | "seed" | "heuristic"</summary>
    public string Source { get; set; } = "llm";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
