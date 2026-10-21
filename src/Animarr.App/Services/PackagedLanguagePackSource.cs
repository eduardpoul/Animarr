using System.Text.Json;
using Animarr.UI.Services;

namespace Animarr.App.Services;

/// <summary>
/// Language packs from the app package. The RCL's static web assets
/// (<c>wwwroot/_content/Animarr.UI/lang/*.json</c>) ship inside the APK for
/// the BlazorWebView, so the UI can read them before it knows any server —
/// Welcome / Discovery used to show bare i18n keys because the HTTP path
/// pointed at the compile-time placeholder host.
/// </summary>
internal sealed class PackagedLanguagePackSource : ILanguagePackSource
{
    public async Task<Dictionary<string, string>?> TryLoadAsync(string language, CancellationToken ct = default)
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(
                $"wwwroot/_content/Animarr.UI/lang/{language}.json");
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct);
        }
        catch (FileNotFoundException) { return null; }
    }
}
