using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Shared;

/// <summary>
/// The Home page is a stack of reorderable sections; each user stores their
/// order + visibility as a small JSON array in
/// <c>UserPreferences.HomeSectionsJson</c> ([{"key":"continue","enabled":true}, …]).
///
/// One place for the key list, the default order and the (de)serialisation so
/// the ProfilePanel editor, the Home renderer and the server-side validator
/// can't drift. The schema deliberately already knows the sections that only
/// exist in the v5 design (thisweek / foryou / discover) — when those features
/// land they slot into the saved order without a migration; until then the
/// settings UI hides them and Home skips them.
/// </summary>
public static class HomeSections
{
    public sealed record Entry(
        [property: JsonPropertyName("key")]     string Key,
        [property: JsonPropertyName("enabled")] bool   Enabled);

    /// <summary>Default order (matches the v5 design board): hero → next-up →
    /// airing calendar → recommendations → library block → discover.</summary>
    public static readonly string[] KnownKeys =
        ["continue", "nextup", "thisweek", "foryou", "library", "discover"];

    /// <summary>Sections that exist in the app TODAY. The settings editor only
    /// offers these (no dead toggles); Home only renders these.</summary>
    public static readonly string[] ImplementedKeys = ["continue", "nextup", "library"];

    public static List<Entry> Defaults() => KnownKeys.Select(k => new Entry(k, true)).ToList();

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Parse a stored value into a full, ordered section list:
    /// unknown keys are dropped, duplicates collapse to the first occurrence,
    /// and known keys missing from the stored value are appended in default
    /// order (so old rows pick up newly-added sections automatically).
    /// Null/blank/broken input → defaults.</summary>
    public static List<Entry> Parse(string? json)
    {
        List<Entry>? stored = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { stored = JsonSerializer.Deserialize<List<Entry>>(json, _json); }
            catch { /* broken value → defaults */ }
        }
        if (stored is null || stored.Count == 0) return Defaults();

        var known  = new HashSet<string>(KnownKeys, StringComparer.OrdinalIgnoreCase);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Entry>();
        foreach (var e in stored)
        {
            if (e?.Key is not { Length: > 0 } k) continue;
            var key = k.ToLowerInvariant();
            if (!known.Contains(key) || !seen.Add(key)) continue;
            result.Add(new Entry(key, e.Enabled));
        }
        foreach (var k in KnownKeys)
            if (seen.Add(k)) result.Add(new Entry(k, true));
        return result;
    }

    public static string Serialize(IEnumerable<Entry> entries)
        => JsonSerializer.Serialize(entries, _json);

    /// <summary>Server-side sanitiser for the PATCH path: whatever the client
    /// sent becomes a canonical parsed-and-reserialised value (or null to fall
    /// back to defaults when the input is empty/broken).</summary>
    public static string? Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var parsed = Parse(json);
        return Serialize(parsed);
    }
}
