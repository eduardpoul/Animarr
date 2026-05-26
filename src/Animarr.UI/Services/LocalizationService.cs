using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.UI.Services;

/// <summary>
/// Client-side localisation. Fetches the active language pack as JSON over
/// HTTP at startup; pages read translations via the indexer (<c>L["key"]</c>)
/// and listen for <see cref="LanguageChanged"/> to re-render when the user
/// switches.
///
/// Originally this read JSON from the server filesystem via
/// <c>IWebHostEnvironment</c> — for WASM/MAUI we read from <c>/lang/{code}.json</c>
/// (served by the API host, which keeps the same JSON files in wwwroot).
/// </summary>
public class LocalizationService
{
    private readonly HttpClient _http;
    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";

    public LocalizationService(HttpClient http) { _http = http; }

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<(string Code, string Label)> SupportedLanguages { get; } =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("uk", "Українська"),
        ("de", "Deutsch"),
        ("es", "Español"),
    ];

    public event Action? LanguageChanged;

    public async Task LoadAsync(string language, CancellationToken ct = default)
    {
        try
        {
            var dict = await _http.GetFromJsonAsync<Dictionary<string, string>>(
                $"/lang/{language}.json", ct);
            if (dict is not null)
            {
                _strings = dict;
                _currentLanguage = language;
                LanguageChanged?.Invoke();
            }
        }
        catch
        {
            // Fall back to keys-as-values if the language pack is missing —
            // mirrors what the server-side reader did when the file was absent.
        }
    }

    public string this[string key] =>
        _strings.TryGetValue(key, out var val) ? val : key;

    public string Get(string key, params object[] args)
    {
        var template = this[key];
        if (args.Length == 0) return template;
        try { return string.Format(template, args); }
        catch { return template; }
    }
}
