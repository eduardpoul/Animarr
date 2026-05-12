namespace Animarr.Web.Data.Models;

public enum RuleScope
{
    Global = 0,
    FolderOverride = 1
}

public class IgnoreRule
{
    public Guid Id { get; set; }

    /// <summary>Glob or wildcard mask, e.g. "fanart*", "poster*", "*.nfo".</summary>
    public string Mask { get; set; } = string.Empty;

    public RuleScope Scope { get; set; } = RuleScope.Global;

    public Guid? FolderId { get; set; }
    public FolderWatcher? Folder { get; set; }
}
