using System.Net;
using Animarr.App.Services;
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
        // compile-time default lands on the developer's box. This seeds the
        // ServerAddressProvider; if ServerRegistryState later hydrates a
        // Current entry from localStorage it overwrites the provider before
        // the first API call lands.
        var serverUrl = Environment.GetEnvironmentVariable("ANIMARR_SERVER_URL") ?? DefaultServerUrl;

        // Shared mutable holder for the active server URL — see
        // ServerAddressProvider's class docs for the "why a separate provider
        // instead of HttpClient.BaseAddress" rationale. Seeded with the
        // compile-time/env default so first-run users still get a sensible
        // probe target before they pick their actual server.
        var addr = new ServerAddressProvider { Current = new Uri(serverUrl) };
        builder.Services.AddSingleton(addr);

        // CookieContainer is required so the v4 auth cookie (AnimarrCookie,
        // issued by /api/auth/login) survives across requests inside the
        // BlazorWebView. Without it the AddCookie handler issues Set-Cookie
        // but HttpClient never echoes it back, so /api/me returns 401 and the
        // user gets bounced to /login on every page load. UseCookies=true on
        // a shared singleton CookieContainer means all IAnimarrApiClient
        // calls share the same auth state for the lifetime of the app.
        //
        // Persistence: in a MAUI Hybrid the in-memory container dies at process
        // exit, so a relaunch logs the user out even though the server-side
        // session is still valid. We re-hydrate from a JSON file on disk
        // BEFORE registering the container, then the CookiePersistHandler
        // (added to the HttpClient pipeline below) snapshots back to disk
        // whenever a response carries Set-Cookie.
        var cookieJar = new CookieContainer();
        CookiePersistence.Load(cookieJar);
        builder.Services.AddSingleton(cookieJar);
        builder.Services.AddSingleton(sp =>
        {
            // Pipeline (outermost → innermost):
            //   ServerAddressHandler — rewrite request authority to the
            //     active server (sidesteps HttpClient.BaseAddress' "started"
            //     lock so switching servers takes effect immediately).
            //   CookiePersistHandler — save the container to disk after any
            //     response that carried Set-Cookie. Keeps the user signed in
            //     across app restarts.
            //   HttpClientHandler — the actual transport, with the shared
            //     CookieContainer driving UseCookies=true.
            var inner = new HttpClientHandler
            {
                UseCookies      = true,
                CookieContainer = cookieJar,
            };
            var persist  = new CookiePersistHandler(cookieJar) { InnerHandler = inner };
            var pipeline = new ServerAddressHandler(addr)      { InnerHandler = persist };

            // BaseAddress is a placeholder that exists ONLY to keep
            // HttpClient.PrepareRequestMessage happy for relative URIs like
            // "api/me" — the handler rewrites the authority to the provider's
            // Current before the request hits the socket. We still seed it
            // with the same serverUrl so any pre-handler diagnostic logging
            // shows a sane host instead of "placeholder.local".
            return new HttpClient(pipeline)
            {
                BaseAddress = new Uri(serverUrl),
                Timeout     = TimeSpan.FromSeconds(30),
            };
        });
        builder.Services.AddAnimarrApiClient();
        builder.Services.AddAnimarrUiState();
        builder.Services.AddScoped<LocalizationService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<ToastService>();

        // mDNS browser singleton — listens for _animarr._tcp.local
        // announcements on every NIC. JS reaches it through the static
        // [JSInvokable] in JsInterop.MdnsBridge, which needs the service to
        // be process-wide reachable; we set MdnsBrowserService.Instance once
        // the container is built (just below) so the dispatch works no matter
        // which Blazor page made the call.
        builder.Services.AddSingleton<MdnsBrowserService>();

        // WatchNextService bridges the Artplayer-side progress events to the
        // Android TV Watch Next channel (Google TV's "Continue watching" row).
        // Singleton so the static Instance accessor used by the JS bridge
        // always resolves to the same instance across page reloads.
        builder.Services.AddSingleton<WatchNextService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Eagerly resolve the browser so its MulticastService starts binding
        // sockets while the splash screen is still up — by the time the user
        // navigates to /discovery there's already a populated cache to show.
        // Also publishes the static accessor used by MdnsBridge.
        var browser = app.Services.GetRequiredService<MdnsBrowserService>();
        MdnsBrowserService.RegisterStaticInstance(browser);

        // Watch Next bridge: publish the singleton so the JS-side static
        // [JSInvokable] in WatchNextBridge can dispatch to the live service
        // regardless of which Blazor circuit fired the call. On non-Android
        // hosts the service is a no-op shell — still resolved so the static
        // accessor is non-null and the JS bridge needs no platform check.
        var watchNext = app.Services.GetRequiredService<WatchNextService>();
        WatchNextService.RegisterStaticInstance(watchNext);

        return app;
    }
}
