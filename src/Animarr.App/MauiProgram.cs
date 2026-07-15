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
                // The web design system (tokens.css) uses ONE face for ui /
                // display / mono alike: Hanken Grotesk. Register the same
                // variable TTF the web ships under all three legacy aliases so
                // every existing FontFamily reference renders exactly like the
                // browser (bold weights via FontAttributes → synthetic bold).
                fonts.AddFont("hanken-grotesk-variable.ttf", "Hanken");
                fonts.AddFont("hanken-grotesk-variable.ttf", "ArchivoBlack");
                fonts.AddFont("hanken-grotesk-variable.ttf", "GeistMono");
                fonts.AddFont("hanken-grotesk-variable.ttf", "Geist");
            });

        // BlazorWebView host plumbing.
        builder.Services.AddMauiBlazorWebView();

#if ANDROID
        // Apply Android WebView tweaks (chrome client for camera permission +
        // MixedContentMode for HTTP-on-HTTPS subresources). We hook into both
        // the property-mapper AND the `BlazorWebViewInitialized` event because
        // the timing of when handler.PlatformView is the bound WebView differs
        // between architectures:
        //   • arm64 (Mi TV / Pixel): mapper runs after CreatePlatformView, so
        //     handler.PlatformView is already the live WebView.
        //   • armeabi-v7a phones: under certain Fast Deploy paths the mapper
        //     fires before the BlazorWebViewHandler completes its setup, and
        //     the MixedContentMode we set gets overwritten by MAUI's own
        //     post-create configuration. The Initialized event is the
        //     framework's "we're done, you may now poke at the WebView"
        //     signal — guaranteed to land last.
        // Both code paths call the same Configure() so they're idempotent.
        static void ConfigureAndroidWebView(global::Android.Webkit.WebView? webView)
        {
            if (webView is null) return;
            try
            {
                webView.SetWebChromeClient(
                    new Platforms.Android.AnimarrWebChromeClient(inner: null));

                // NOTE: We tried installing a custom WebViewClient here to
                // proxy http:// subresources around Chromium's mixed-content
                // gate, but that broke the BlazorWebView bundle entirely —
                // MAUI's internal WebViewClient is the only thing wiring the
                // https://0.0.0.x/ virtual host to the WebViewAssetLoader,
                // and replacing it stops Blazor from loading. Until we find
                // a way to chain to the framework client we rely on
                // MixedContentMode below + the in-page JS image-URL rewriter
                // (animarr-img-proxy.js) for the http-on-https case.
                if (webView.Settings is not null)
                {
                    webView.Settings.MixedContentMode = global::Android.Webkit.MixedContentHandling.AlwaysAllow;

                    // Disable the WebView HTTP cache. Our bundle (index.html,
                    // animarr-player.js, CSS) is served LOCALLY from the APK via
                    // BlazorWebView's WebViewAssetLoader, so caching buys almost
                    // nothing — but it actively hurts: after an app update the
                    // WebView kept serving the *previous* index.html from cache
                    // while the JS sub-resources refreshed, so gate changes in
                    // index.html (e.g. the native-player gate) silently never
                    // took effect and we chased phantom bugs. LoadNoCache forces
                    // every request through the asset loader, guaranteeing the
                    // shipped assets are what actually run.
                    webView.Settings.CacheMode = global::Android.Webkit.CacheModes.NoCache;

                    Android.Util.Log.Info("Animarr.WebView",
                        $"Configured: MixedContentMode={webView.Settings.MixedContentMode}, CacheMode={webView.Settings.CacheMode}, chromeClient=AnimarrWebChromeClient");
                }

                // CacheMode=NoCache stops NEW caching, but it does NOT evict
                // what's already on disk. The WebViewAssetLoader sub-resources
                // (the content-hashed scoped-CSS bundle + app.css, pulled via
                // @import from the unhashed Animarr.App.styles.css) were cached
                // by a PRIOR app version and kept being served stale: the main
                // document (index.html) refreshed so inline-style changes landed
                // — but CSS-only changes in the scoped bundle (mobile drilldown
                // rail/tab hides) silently never did, because the cached
                // Animarr.App.styles.css still @imported the old bundle hash.
                // ClearCache(true) purges that lingering disk cache — but only
                // ONCE per installed build, not on every launch. The per-launch
                // purge was disk I/O on the cold-start path; gating it to a
                // build-stamp change keeps the stale-asset guarantee (a freshly
                // installed APK always purges first) while dropping the cost
                // from every later launch of the SAME build — the end-user's
                // daily case on the TV.
                if (ShouldPurgeWebViewCacheForThisBuild())
                {
                    webView.ClearCache(true);
                    Android.Util.Log.Info("Animarr.WebView", "WebView cache purged for new build stamp");
                }

                // Inject the real status-bar / nav-bar heights as CSS custom
                // properties NOW — this hook runs on BlazorWebViewInitialized,
                // the one moment the WebView's JS context is guaranteed live.
                // MainActivity computed them from resource dimens in OnCreate.
                // Without this the TopBar / hero chips render under the status
                // bar because Android WebView never fills env(safe-area-*).
                try
                {
                    var snippet = MainActivity.SafeAreaCssSnippet();
                    webView.EvaluateJavascript(snippet, null);
                    Android.Util.Log.Info("Animarr.WebView", $"Safe-area pushed: {snippet}");
                }
                catch (Exception ex2)
                {
                    Android.Util.Log.Warn("Animarr.WebView", $"Safe-area push failed: {ex2.Message}");
                }

                // Publish the loopback media-proxy base into the page. The web
                // player (animarr-player.js animarrSetApiBase) routes all its
                // server fetches through this instead of the plain-HTTP LAN URL,
                // so hls.js talks to http://127.0.0.1:<port> (no mixed content,
                // no base64 bridge → no freeze). Set as early as possible; the
                // page reads it when MainLayout applies the server base.
                try
                {
                    var proxy = Services.LocalMediaProxyService.Instance;
                    if (proxy is { Port: > 0 })
                    {
                        webView.EvaluateJavascript(
                            $"window.animarrLocalProxyBase = '{proxy.BaseUrl}';", null);
                        Android.Util.Log.Info("Animarr.WebView",
                            $"Local proxy base pushed: {proxy.BaseUrl}");
                    }
                }
                catch (Exception exP)
                {
                    Android.Util.Log.Warn("Animarr.WebView", $"Proxy-base push failed: {exP.Message}");
                }
                // Phase 2b (2026-05-27): make the WebView transparent so the
                // TextureView MainActivity inserted at the bottom of DecorView
                // shows through during playback. Body remains opaque by default
                // (via tokens.css' :root background) so non-player pages still
                // paint their dark chrome; the player overlay (animarr-player.js
                // attach()) adds a `native-playback` class to <body> that wipes
                // the background, exposing the video underneath the HUD.
                webView.SetBackgroundColor(global::Android.Graphics.Color.Transparent);

                // …but a transparent WebView isn't enough on its own. The
                // TextureView lives at DecorView index 0 (the very back); the
                // WebView sits inside several parent ViewGroups (MAUI's layout +
                // android.R.id.content) that default to the theme's OPAQUE dark
                // background. Those intermediate layers paint BETWEEN the
                // transparent WebView and the TextureView, so the decoded video
                // (confirmed rendering — "VideoFormat decoded: h264 …" in
                // logcat) was being occluded → user saw a dark screen with HUD +
                // audio but no picture. Walk up the WebView's ancestor chain and
                // clear each background so the back-most TextureView shows
                // through during native playback. Safe in the non-playback case:
                // the WebView body is opaque dark then, so transparent ancestors
                // are never visible. We stop before DecorView (its background is
                // BEHIND the TextureView, so it never occludes, and we don't
                // want to disturb the window root).
                try
                {
                    global::Android.Views.View? v =
                        webView.Parent as global::Android.Views.View;
                    for (int depth = 0; depth < 12 && v is not null; depth++)
                    {
                        if (v is global::Android.Views.ViewGroup)
                            v.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
                        v = v.Parent as global::Android.Views.View;
                    }
                    Android.Util.Log.Info("Animarr.WebView",
                        "Cleared WebView ancestor backgrounds for native-video passthrough");
                }
                catch (Exception exAnc)
                {
                    Android.Util.Log.Warn("Animarr.WebView",
                        $"Ancestor-transparency walk failed: {exAnc.Message}");
                }

                // Kill the Android 12+ elastic "stretch" overscroll — the
                // jelly / rubber-band the user feels when dragging a finger
                // sideways (the catalog hero is NOT a horizontal scroller, so
                // the drag hits the root document and Chromium stretches the
                // whole page) or vertically on a short page with nothing to
                // scroll. CSS `overscroll-behavior:none` on html/body does NOT
                // reliably suppress this on Android WebView — the renderer's
                // root overscroll honours the host View's OverScrollMode, so
                // setting it to Never is the actual fix. Nested HTML carousels
                // (chip rails, folder strips) keep their own
                // `overscroll-behavior:none` in components.css for the
                // end-of-list bounce. Result: a solid, non-jelly "monolith".
                webView.OverScrollMode = global::Android.Views.OverScrollMode.Never;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Animarr.WebView", $"ConfigureAndroidWebView failed: {ex.Message}");
            }
        }

        // True exactly once per installed build: compares the compile-time
        // AnimarrBuildStamp (assembly metadata, see csproj) against the last
        // value we persisted. New build → purge + remember. DEBUG always
        // purges so hot dev iterations never serve stale assets. Any failure
        // falls back to the old always-purge so correctness never regresses.
        static bool ShouldPurgeWebViewCacheForThisBuild()
        {
#if DEBUG
            return true;
#else
            try
            {
                var stamp = "";
                foreach (var a in typeof(MauiProgram).Assembly
                             .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                    if (a is System.Reflection.AssemblyMetadataAttribute m && m.Key == "AnimarrBuildStamp")
                    { stamp = m.Value ?? ""; break; }

                const string key = "animarr_webview_cache_stamp";
                if (Microsoft.Maui.Storage.Preferences.Get(key, "") == stamp) return false;
                Microsoft.Maui.Storage.Preferences.Set(key, stamp);
                return true;
            }
            catch { return true; }
#endif
        }

        void OnBlazorWebViewInitialized(object? sender,
            Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
            ConfigureAndroidWebView(e.WebView as global::Android.Webkit.WebView);
        }

        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler
            .BlazorWebViewMapper.AppendToMapping(
                "AnimarrWebViewSetup",
                (handler, view) =>
                {
                    ConfigureAndroidWebView(handler.PlatformView as global::Android.Webkit.WebView);
                    // Also wire the Initialized event for the late retry — it
                    // fires once per BlazorWebView lifetime, AFTER MAUI's own
                    // post-create configuration would have stomped our
                    // MixedContentMode setting.
                    if (view is Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebView bwv)
                    {
                        bwv.BlazorWebViewInitialized -= OnBlazorWebViewInitialized;
                        bwv.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
                    }
                });
#endif

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
        // Mirror the restored cookies into the WebView store too — its <img>
        // fetches (episode thumbs on cookie-gated servers) authenticate via
        // the WEBVIEW's cookies, not the HttpClient container.
        CookiePersistence.SyncToWebView(cookieJar);
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

        // NativePlayerService (Phase 2 ExoPlayer-backed native playback).
        // Renders into the TextureView MainActivity inserts behind the
        // transparent BlazorWebView. Used on ALL Android hosts now (TV *and*
        // phone/tablet): inside the WebView the app is served from the
        // https://0.0.0.1/ virtual host, so hls.js segment fetches to a
        // plain-HTTP LAN server are "mixed content" and have to be proxied as
        // base64 over the JS↔.NET bridge — that bridge's GC churn freezes
        // playback. ExoPlayer fetches with its own native HTTP stack (no
        // WebView, no bridge), so playback is smooth + HDR/DV passes through.
        // The static Instance (set just below build()) is what the JS bridge
        // NativePlayerBridge dispatches through. On non-Android targets the
        // service is a harmless no-op shell (IsAvailable => false).
        builder.Services.AddSingleton<NativePlayerService>();

        // Subnet probe — TCP fallback for when mDNS broadcasts can't cross
        // the home-WiFi AP's wired/wireless boundary (very common on consumer
        // routers). Discovery.razor calls this only when the mDNS browse came
        // back empty, so cheap setups still get a working "find my server"
        // flow without the user having to memorise an IP.
        builder.Services.AddSingleton<SubnetProbeService>();

        // Loopback media proxy. The WebView (served from https://0.0.0.1) can't
        // fetch the plain-HTTP LAN server directly (mixed content). Rather than
        // base64 every segment over the JS bridge (which froze playback), we run
        // a tiny reverse-proxy on http://127.0.0.1 — a "potentially trustworthy"
        // origin Chromium does NOT mixed-content-block — and forward to the real
        // server. hls.js streams from loopback over a normal socket, so the web
        // player runs smoothly with the HUD overlaid, on phone AND TV, and the
        // self-hoster needs no certs / no server HTTPS. See LocalMediaProxyService.
        builder.Services.AddSingleton<LocalMediaProxyService>();

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

        // Same static-accessor pattern for the subnet probe — JsInterop layer
        // dispatches through SubnetProbeBridge without juggling a per-page
        // DotNetObjectReference.
        var subnet = app.Services.GetRequiredService<SubnetProbeService>();
        JsInterop.SubnetProbeBridge.RegisterStaticInstance(subnet);

        // Native player: publish the singleton's static accessor so the JS
        // bridge (NativePlayerBridge JSInvokables → animarrNativePlayer.*) can
        // dispatch to the live ExoPlayer service no matter which Blazor circuit
        // made the call. WITHOUT this, NativePlayerService.Instance stays null,
        // NativePlayerBridge.IsAvailable() returns false, and the JS gate falls
        // back to the web (hls.js) player on every host — which is exactly the
        // base64-bridge path that freezes phone playback. Resolved eagerly (the
        // ctor only stashes the logger; ExoPlayer itself is built lazily on the
        // first PlayAsync) so Instance is ready before the first navigation.
        var nativePlayer = app.Services.GetRequiredService<NativePlayerService>();
        NativePlayerService.RegisterStaticInstance(nativePlayer);

        // Start the loopback media proxy NOW so its port is known before the
        // WebView loads (ConfigureAndroidWebView pushes the proxy base URL into
        // the page on init). Start() is a no-op-safe try/catch; the static
        // accessor lets ConfigureAndroidWebView read the chosen port.
        var mediaProxy = app.Services.GetRequiredService<LocalMediaProxyService>();
        mediaProxy.Start();
        LocalMediaProxyService.RegisterStaticInstance(mediaProxy);

        return app;
    }
}
