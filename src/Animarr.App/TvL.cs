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
                // Remembered for the next launch: the "Who's watching" picker
                // and pairing run before any profile (and its language) is known.
                try { Microsoft.Maui.Storage.Preferences.Set(LastLangKey, code); } catch { }
            }
        }
        catch { /* offline / older server — fallbacks apply */ }
    }

    private const string LastLangKey = "animarr_tv_lang";

    /// <summary>Load the pack of the language last used on this TV (before a
    /// profile is signed in). Defaults to ru.</summary>
    public static Task LoadLastAsync(HttpClient http)
    {
        string? last = null;
        try { last = Microsoft.Maui.Storage.Preferences.Get(LastLangKey, "ru"); } catch { }
        return LoadAsync(http, last);
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
