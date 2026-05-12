namespace Animarr.Web.Data.Models;

public enum PatternScope
{
    Global = 0,
    FolderOverride = 1
}

public class RenamePattern
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Regex pattern to extract season/episode from filename.</summary>
    public string Pattern { get; set; } = string.Empty;

    public PatternScope Scope { get; set; } = PatternScope.Global;

    /// <summary>When Scope=FolderOverride and IsExcluded=true — suppress the global pattern for this folder.</summary>
    public bool IsExcluded { get; set; } = false;

    /// <summary>When IsExcluded=true, references the Global pattern being suppressed for this folder.</summary>
    public Guid? GlobalPatternId { get; set; }

    /// <summary>Lower value = higher priority.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Built-in patterns cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; } = false;

    /// <summary>null = applies to all folder types; Movie = only movie folders; Series = only series/auto folders.</summary>
    public FolderType? ApplicableTo { get; set; }

    public Guid? FolderId { get; set; }
    public FolderWatcher? Folder { get; set; }
}
