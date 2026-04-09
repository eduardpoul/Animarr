using System.Text.Json;

namespace Animarr.Web.Services;

public class LocalizationService
{
    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<(string Code, string Label)> SupportedLanguages { get; } =
        [("en", "English"), ("ru", "Русский")];

    public event Action? LanguageChanged;

    public async Task LoadAsync(string language, IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Localization", $"{language}.json");
        if (!File.Exists(path)) return;

        await using var fs = File.OpenRead(path);
        var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs);
        if (dict is not null)
        {
            _strings = dict;
            _currentLanguage = language;
            LanguageChanged?.Invoke();
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
