using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.UI.Services;

/// <summary>
/// Reads a language pack without the network. The MAUI app registers one that
/// opens the copy packaged in the APK: the packs ship there anyway, and on the
/// first screens (Welcome / Discovery) there is no server yet to fetch them
/// from, so the HTTP path showed bare i18n keys.
/// </summary>
public interface ILanguagePackSource
{
    /// <summary>The pack for <paramref name="language"/>, or null when this
    /// source doesn't have it.</summary>
    Task<Dictionary<string, string>?> TryLoadAsync(string language, CancellationToken ct = default);
}

/// <summary>
/// Client-side localisation. Loads the active language pack as JSON at
/// startup; pages read translations via the indexer (<c>L["key"]</c>)
/// and listen for <see cref="LanguageChanged"/> to re-render when the user
/// switches.
///
/// Sources, in order: any registered <see cref="ILanguagePackSource"/> (the
/// MAUI app's packaged copy), then <c>_content/Animarr.UI/lang/{code}.json</c>
/// over HTTP (the WASM client, served by the API host).
/// </summary>
public class LocalizationService
{
    private readonly HttpClient _http;
    private readonly ILanguagePackSource[] _local;
    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";

    public LocalizationService(HttpClient http, IEnumerable<ILanguagePackSource> local)
    {
        _http  = http;
        _local = local.ToArray();
    }

    public string CurrentLanguage => _currentLanguage;

    /// <summary>True once any pack has been loaded (until then the indexer
    /// returns keys).</summary>
    public bool IsLoaded => _strings.Count > 0;

    public IReadOnlyList<(string Code, string Label)> SupportedLanguages { get; } =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("uk", "Українська"),
        ("de", "Deutsch"),
        ("es", "Español"),
    ];

    public event Action? LanguageChanged;

    /// <summary>Language to boot with before the user's server-side preference
    /// is known: the one cached from the last session, else the device's UI
    /// language when we ship it, else English.</summary>
    public string ResolveBootLanguage(string? cached)
    {
        if (IsSupported(cached)) return cached!;
        var device = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsSupported(device) ? device : "en";
    }

    private bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && SupportedLanguages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    public async Task LoadAsync(string language, CancellationToken ct = default)
    {
        // Fallback chain: requested language → "en" → keys-as-values, so a
        // missing pack shows English instead of bare i18n keys.
        if (await TryLoadAsync(language, ct)) return;
        if (language != "en") await TryLoadAsync("en", ct);
    }

    private async Task<bool> TryLoadAsync(string language, CancellationToken ct)
    {
        foreach (var source in _local)
        {
            try
            {
                if (await source.TryLoadAsync(language, ct) is { Count: > 0 } dict)
                    return Apply(language, dict);
            }
            catch { /* fall through to the next source */ }
        }

        try
        {
            var dict = await _http.GetFromJsonAsync<Dictionary<string, string>>(
                $"_content/Animarr.UI/lang/{language}.json", ct);
            if (dict is not null) return Apply(language, dict);
        }
        catch
        {
            // Network / parse failure — caller will try the fallback.
        }
        return false;
    }

    private bool Apply(string language, Dictionary<string, string> dict)
    {
        _strings = dict;
        _currentLanguage = language;
        LanguageChanged?.Invoke();
        return true;
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

    /// <summary>Every loaded string whose key starts with <paramref name="prefix"/>,
    /// keyed without it — e.g. all "player." strings handed to animarr-player.js
    /// in one go, so the JS HUD needs no per-string plumbing.</summary>
    public Dictionary<string, string> WithPrefix(string prefix) =>
        _strings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value);
}
