namespace Animarr.UI.Services;

/// <summary>
/// Cross-page state for the "which catalog folder is selected" pill that lives
/// in TopBar. The folder strip is rendered above the route so we can't pass
/// it through a parameter — Catalog reads it from here on mount, and TopBar
/// writes to it from its click handlers.
///
/// Mirrors the v4 design's <c>window.__folderJump</c> global without leaking
/// state into JS.
/// </summary>
public sealed class FolderJumpService
{
    public string? ActiveFolderName { get; private set; }
    public Guid?   ActiveFolderId   { get; private set; }

    public event Action? OnChange;

    public void SetActiveFolder(string? name)
    {
        ActiveFolderName = name;
        ActiveFolderId = null;
        OnChange?.Invoke();
    }

    public void SetActiveFolderId(Guid? id, string? name)
    {
        ActiveFolderId   = id;
        ActiveFolderName = name;
        OnChange?.Invoke();
    }
}
