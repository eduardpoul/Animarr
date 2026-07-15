using System.Net.Http.Json;

namespace Animarr.App;

/// <summary>
/// TV-side localization: loads the SAME lang pack the web UI uses
/// (<c>/_content/Animarr.UI/lang/{code}.json</c>, served by the connected
/// server) and resolves web keys. TV-only strings that have no pack key pass
/// ru/en fallbacks — Russian when the active pack is ru, English otherwise
/// (uk/de/es packs cover the shared keys; the few TV-only strings read in
/// English there until keys are added to the packs).
/// </summary>
internal static class TvL
{
    private static Dictionary<string, string> _map = new();

    public static string Lang { get; private set; } = "ru";

    /// <summary>Fetch + cache the pack. No-op when the language is already
    /// active; failures keep the previous pack (fallbacks still work).</summary>
    public static async Task LoadAsync(HttpClient http, string? language)
    {
        var code = string.IsNullOrWhiteSpace(language) ? "ru" : language!.ToLowerInvariant();
        if (code == Lang && _map.Count > 0) return;
        try
        {
            var map = await http.GetFromJsonAsync<Dictionary<string, string>>(
                $"/_content/Animarr.UI/lang/{code}.json");
            if (map is { Count: > 0 })
            {
                _map = map;
                Lang = code;
            }
        }
        catch { /* offline / older server — fallbacks apply */ }
    }

    /// <summary>Pack value for <paramref name="key"/>, else the ru/en fallback.</summary>
    public static string T(string key, string ru, string en)
        => _map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : Lang == "ru" ? ru : en;

    /// <summary>Format helper for "{0}"-style pack strings.</summary>
    public static string F(string key, string ru, string en, params object[] args)
        => string.Format(T(key, ru, en), args);
}
