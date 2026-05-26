using Animarr.Shared;
using Animarr.UI;
using Animarr.UI.Services;
using Microsoft.Extensions.Logging;

namespace Animarr.App;

public static class MauiProgram
{
    /// <summary>
    /// Override at runtime via <c>SERVER_URL</c> env var (Android / desktop
    /// debug builds) or compile-time via <c>AnimarrServerUrl</c> MSBuild
    /// property. Falls back to the developer's tower.one box for sanity
    /// during local builds — release builds should always pass an explicit
    /// URL because there's no guarantee tower.one is reachable.
    /// </summary>
    public const string DefaultServerUrl = "https://animarr.tower.one";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // BlazorWebView host plumbing.
        builder.Services.AddMauiBlazorWebView();

        // Resolve the Animarr server URL — env var wins, otherwise the
        // compile-time default lands on the developer's box.
        var serverUrl = Environment.GetEnvironmentVariable("ANIMARR_SERVER_URL") ?? DefaultServerUrl;

        // Single HttpClient with the server's BaseAddress so every
        // IAnimarrApiClient call resolves to /api/* on the right host.
        builder.Services.AddSingleton(sp => new HttpClient
        {
            BaseAddress = new Uri(serverUrl),
            Timeout     = TimeSpan.FromSeconds(30),
        });
        builder.Services.AddAnimarrApiClient();
        builder.Services.AddAnimarrUiState();
        builder.Services.AddScoped<LocalizationService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<ToastService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
