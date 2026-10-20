namespace Animarr.Shared;

/// <summary>
/// Relative destination path inside a download folder — what the Add-download
/// drawer's subfolder picker produces ("Cowboy Bebop/Season 2") and what the
/// server combines with the folder's root. Canonical form is '/'-separated
/// with no empty, "." or ".." segments, so a value can never climb out of the
/// folder it's relative to.
/// </summary>
public static class SubfolderPath
{
    /// <summary>Characters a NEW folder name may not contain. Windows' set, even
    /// on a Linux server: libraries often live on SMB/NTFS shares where ':' or
    /// '?' fail at write time, long after the picker accepted the name.</summary>
    public const string InvalidNameChars = "/\\:*?\"<>|";

    /// <summary>Split any '/' or '\' path into clean segments: trimmed, with
    /// empty / "." / ".." segments and control characters dropped.</summary>
    public static string[] Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        return path.Split('/', '\\')
            .Select(s => new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim())
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .ToArray();
    }

    /// <summary>Canonical '/'-joined form, or null when nothing is left.</summary>
    public static string? Normalize(string? path)
    {
        var parts = Split(path);
        return parts.Length == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="name"/> is usable as a single new
    /// folder name (one segment — no separators).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n == "." || n == ".." || n.Length > 255) return false;
        // Windows silently strips a trailing dot, so "Show." would land as "Show".
        if (n.EndsWith('.')) return false;
        return !n.Any(c => char.IsControl(c) || InvalidNameChars.Contains(c));
    }
}
