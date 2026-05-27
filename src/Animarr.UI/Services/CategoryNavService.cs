namespace Animarr.UI.Services;

/// <summary>
/// Cross-page state for the "which category is selected" tab strip that lives
/// in TopBar (v5). Mirrors <see cref="FolderJumpService"/> but for categories.
///
/// TopBar fetches the category list once on first render and emits clicks
/// here; Home subscribes to <see cref="OnChange"/> and reapplies its filter
/// in response. ActiveCategoryName=null means "All".
///
/// Per the v5 design board, categories replace folders as the primary
/// catalog filter — folders are now an admin concept (Server Settings →
/// Folders). FolderJumpService stays around for any flow that wants
/// folder-level navigation, but Home leans on this one.
/// </summary>
public sealed class CategoryNavService
{
    public string? ActiveCategoryName { get; private set; }

    public event Action? OnChange;

    public void SetActiveCategory(string? name)
    {
        // Empty string from a click also normalises to null so we don't end
        // up with two ways to express "All".
        ActiveCategoryName = string.IsNullOrWhiteSpace(name) ? null : name;
        OnChange?.Invoke();
    }
}
