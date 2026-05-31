namespace Animarr.Web.Services;

/// <summary>
/// Resolves the on-disk location of downloaded GGUF models for the built-in
/// ("embedded") llama.cpp provider.
///
/// Mirrors <see cref="MediaCachePaths"/>: models live next to the SQLite
/// database (e.g. <c>/app/data/models/</c> in the docker image, on the
/// persistent <c>animarr-data</c> volume) so multi-GB weights survive container
/// recreation and never bloat the image.
/// </summary>
public sealed class ModelPaths
{
    /// <summary>Absolute path to the models directory. Created on construction.</summary>
    public string ModelsRoot { get; }

    public ModelPaths(IConfiguration cfg)
    {
        // Mirror MediaCachePaths: derive from the same data dir the SQLite db
        // lives in. Falls back to current dir for the dev profile.
        var conn   = cfg.GetConnectionString("DefaultConnection") ?? "";
        var dbPath = conn.Replace("Data Source=", "").Trim();
        var dbDir  = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dbDir)) dbDir = ".";
        ModelsRoot = Path.GetFullPath(Path.Combine(dbDir, "models"));
        Directory.CreateDirectory(ModelsRoot);
    }

    /// <summary>
    /// Full path for a model file by name, or <c>null</c> when the name escapes
    /// the models directory. Defends against path traversal exactly like the
    /// <c>/api/image</c> and HLS-segment handlers: reject separators up-front,
    /// then canonicalise and assert the result stays under <see cref="ModelsRoot"/>.
    /// </summary>
    public string? ResolveModelFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) return null;
        var full = Path.GetFullPath(Path.Combine(ModelsRoot, fileName));
        var root = ModelsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? ModelsRoot
            : ModelsRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Enumerate installed <c>*.gguf</c> files (name + size in bytes).</summary>
    public IReadOnlyList<(string FileName, long Bytes)> ListInstalled()
    {
        try
        {
            return Directory.EnumerateFiles(ModelsRoot, "*.gguf", SearchOption.TopDirectoryOnly)
                .Select(p => (Path.GetFileName(p), new FileInfo(p).Length))
                .OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<(string, long)>();
        }
    }

    /// <summary>Free space (bytes) on the volume holding the models dir, or 0 if unknown.</summary>
    public long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(ModelsRoot)!).AvailableFreeSpace; }
        catch { return 0; }
    }
}
