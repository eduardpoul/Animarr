using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity;

namespace Animarr.App;

// Java-side class name is fixed to `com.animarr.app.MainActivity` so the
// merged manifest's <activity android:name=".MainActivity"> entry (default
// expansion of namespace + class name) matches the DEX class. Without an
// explicit Name= the binding generator emits a `crc64<hash>.MainActivity`
// stub and the launcher resolution fails with `ClassNotFoundException` at
// first run on Android TV — silent black-screen crash.
[Activity(
    Name = "com.animarr.app.MainActivity",
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
                         | ConfigChanges.Orientation
                         | ConfigChanges.UiMode
                         | ConfigChanges.ScreenLayout
                         | ConfigChanges.SmallestScreenSize
                         | ConfigChanges.Density)]
// Phone + Android TV launchers — Leanback so TV's home grid lists Animarr.
[IntentFilter(
    new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLauncher, "android.intent.category.LEANBACK_LAUNCHER" })]
// "Open in Animarr" — file managers / browsers send a video URL or local
// stream here. Schemes + MIME types match the AndroidManifest entry we used
// to ship in the static XML before MAUI's manifest merger started rejecting
// the duplicate.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "http", "https" },
    DataMimeTypes = new[]
    {
        "application/vnd.apple.mpegurl",
        "application/x-mpegURL",
        "video/mp4",
        "video/x-matroska",
    },
    Label = "Open in Animarr")]
// Watch Next / Continue Watching deep link.
// Google TV's home-screen carousel ("Continue watching") issues an Intent.ActionView
// with the URI we stored on the WatchNextProgram row (animarr://play/{mediaId}).
// When the user taps a card we want to land directly on the media detail / player
// instead of the default launch route. SingleTop + OnNewIntent overrides below
// ensure repeated taps don't spawn duplicate activities — the existing instance
// just gets a new Intent to consume.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "animarr" },
    Label = "Resume in Animarr")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Most-recent deep-link target the activity received but hasn't handed
    /// off to Blazor yet. <see cref="JsInterop.PlatformBridge.ConsumePendingDeepLink"/>
    /// is the JS-side consumer that drains this on Blazor mount.
    ///
    /// Stored as a plain field rather than going through DI because the
    /// activity is constructed by the Android framework, not by our
    /// DI container, and the intent can arrive before MauiApp is built.
    /// </summary>
    public static string? PendingDeepLinkMediaId { get; private set; }

    /// <summary>Atomic read + clear of <see cref="PendingDeepLinkMediaId"/> —
    /// the JS bridge calls this once on Blazor mount so the same Intent
    /// doesn't keep redirecting on subsequent renders.</summary>
    public static string? DrainPendingDeepLink()
    {
        var id = PendingDeepLinkMediaId;
        PendingDeepLinkMediaId = null;
        return id;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // MulticastLock is acquired earlier in MainApplication.OnCreate so it's
        // already held by the time MauiProgram.CreateMauiApp resolves
        // MdnsBrowserService. Activity.OnCreate is too late for that.
        base.OnCreate(savedInstanceState);

        // Edge-to-edge: paint behind the system bars so the cinematic backdrop
        // fills the whole screen rather than getting boxed in by a black
        // status / nav strip. The Blazor side then uses
        //   padding-top: env(safe-area-inset-top)
        // on top-pinned surfaces (TopBar, drawer headers, modal headers) and
        //   padding-bottom: env(safe-area-inset-bottom)
        // on the mobile bottom-tab so content doesn't slide under the
        // gesture indicator. On TV both insets are 0 — no-op.
        try
        {
            if (Window is { } w)
            {
                AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(w, false);
                // Transparent system bars so the backdrop shows through. The
                // gradient washes at the top/bottom of the page own the
                // contrast — we don't need an opaque status bar overlay.
                w.SetStatusBarColor(Android.Graphics.Color.Transparent);
                w.SetNavigationBarColor(Android.Graphics.Color.Transparent);

                // NOTE: an earlier attempt made the window translucent to let a
                // BELOW-window video surface show through the transparent WebView
                // — that never worked on this device (the WebView layer occluded
                // it). The working approach renders the video ABOVE the window
                // (SurfaceView.SetZOrderOnTop) in a centred letterbox band, with
                // the HUD bars showing around it, so the window stays OPAQUE here
                // (its black background paints the letterbox bars).
            }
        }
        catch
        {
            // OEM ROMs (Mi TV, Huawei) sometimes reject SetDecorFitsSystemWindows
            // on older API levels — fall back to the default layout, the UI
            // still works just with a ~24px black strip at the top.
        }

        // Android WebView (unlike iOS WKWebView) does NOT populate the CSS
        // env(safe-area-inset-*) variables even in edge-to-edge mode — they
        // resolve to 0, so padding-top: env(safe-area-inset-top) collapses and
        // the TopBar / hero chips render UNDER the status-bar clock. We read
        // the real status-bar + nav-bar heights and stash them in a static so
        // MauiProgram's BlazorWebViewInitialized hook (which fires once the
        // WebView JS context is guaranteed live) can inject them as CSS custom
        // properties. Doing the push from the Initialized event — rather than
        // from a window-insets listener that fires before the WebView exists —
        // is what makes this actually stick.
        try
        {
            // status_bar_height / navigation_bar_height are stable platform
            // dimens. Reading them here is synchronous + reliable, no insets
            // dispatch race. Convert device px → CSS px (÷ density).
            var res = Resources;
            float density = res?.DisplayMetrics?.Density ?? 1f;
            if (density <= 0) density = 1f;

            int statusId = res?.GetIdentifier("status_bar_height", "dimen", "android") ?? 0;
            int navId    = res?.GetIdentifier("navigation_bar_height", "dimen", "android") ?? 0;
            int statusPx = statusId > 0 ? res!.GetDimensionPixelSize(statusId) : 0;
            int navPx    = navId > 0 ? res!.GetDimensionPixelSize(navId) : 0;

            SafeTopCssPx    = statusPx / density;
            SafeBottomCssPx = navPx / density;
        }
        catch { /* dimens unavailable — vars stay 0, env() fallback applies */ }

        // Also keep a window-insets listener as a secondary updater so a
        // rotation / gesture-bar change refreshes the vars live (the
        // resource dimens above don't track a hidden nav bar, e.g. fullscreen
        // immersive). Pushes through the same WebView path.
        try
        {
            if (Window?.DecorView is { } decorView)
            {
                AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(decorView,
                    new InsetsListener(this));
            }
        }
        catch { /* insets unavailable — resource dimens above already applied */ }

        CaptureDeepLink(Intent);

        // Back-gesture handling temporarily disabled. The earlier
        // OnBackPressedDispatcher.AddCallback intercept appeared to
        // collide with Blazor's own router-level history handling — the
        // catalog ended up empty after the first launch, suggesting the
        // callback fired and popped history mid-bootstrap. Until we have
        // a cleaner integration with Blazor.Router (which already
        // supports browser back via window.history), the system default
        // is back. The user will lose in-app navigation back, but the
        // app's own back affordances (chips, drawer back arrows) still
        // work, and the back gesture cleanly closes the app instead of
        // crashing into a blank page.

        // Native ExoPlayer video surface is now a TextureView created by the
        // native PlayerPage (added to its VideoHost + registered via
        // NativePlayerService.RegisterTextureView), so the activity no longer
        // inserts a DecorView SurfaceView. A TextureView composites inside the
        // normal view tree, so the XAML HUD overlays it cleanly — no hole-punch,
        // no z-order fight (the SurfaceView path was for the old WebView shell).
    }

    protected override void OnNewIntent(Intent? intent)
    {
        // Warm restart — activity already running (SingleTop), new Intent
        // delivered for a fresh launcher tap. Update the pending target
        // and replace the activity's intent so future getIntent() reads see it.
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
        CaptureDeepLink(intent);
    }

    /// <summary>Forward Android's system back gesture (edge swipe / hardware
    /// BACK) to the WebView's history stack. The default
    /// <c>MauiAppCompatActivity</c> behaviour just finish()s the activity,
    /// which closes the entire app the very first time the user swipes —
    /// even from a deeply nested route.
    ///
    /// Three-step decision in JS:
    ///   1. <c>window.animarrHandleBack()</c> — closes the topmost open
    ///      drawer / modal (or the fullscreen player). Returns 'consumed'
    ///      when something was actually dismissed.
    ///   2. Otherwise <c>window.history.back()</c> pops one route — the
    ///      Blazor router treats it as a normal nav and updates the page.
    ///   3. Otherwise (history length == 1, the user is on Welcome /
    ///      catalog root) we Finish() the activity so the launcher
    ///      reclaims focus instead of leaving the app on a dead screen.
    ///
    /// EvaluateJavascript is async — the renderer runs the snippet on
    /// its own thread, then ValueCallback gets the result back on the
    /// UI thread (BackResultCallback below). We don't fall through to
    /// base.OnBackPressed() here because the system handler would
    /// instantly finish() the activity and race the async result.</summary>
    // Route D-pad keys to the native player while it's on screen so OK toggles
    // play/pause and ◀/▶ seek — a TV player drives off the remote, not focused
    // on-screen buttons. Handled keys are swallowed so they don't also move
    // focus in the (hidden) HUD underneath.
    public override bool OnKeyDown(Android.Views.Keycode keyCode, Android.Views.KeyEvent? e)
    {
        if (PlayerPage.HandleGlobalKey((int)keyCode)) return true;
        return base.OnKeyDown(keyCode, e);
    }

#pragma warning disable CA1422 // OnBackPressed is deprecated on API 33+ but still fires
    public override void OnBackPressed()
    {
        // Player HUD open → Back closes the HUD, not the page.
        if (PlayerPage.HandleGlobalBack()) return;

        // Native TV flow (no WebView on these pages): pop the MAUI navigation
        // stack — player → detail → catalog — before anything else, so BACK
        // doesn't finish() the whole app from a nested native screen. On phone
        // the root is a plain MainPage (stack depth 1) so this no-ops and the
        // WebView history logic below runs as before.
        try
        {
            var win = Microsoft.Maui.Controls.Application.Current?.Windows;
            var nav = win is { Count: > 0 } ? win[0].Page?.Navigation : null;
            if (nav?.NavigationStack is { Count: > 1 })
            {
                _ = nav.PopAsync();
                return;
            }
        }
        catch { /* fall through to the WebView / default handler */ }

        try
        {
            var webView = FindWebView(Window?.DecorView);
            if (webView is not null)
            {
                webView.EvaluateJavascript(
                    "(function(){" +
                    "  if (window.animarrHandleBack && window.animarrHandleBack()) return 'consumed';" +
                    "  if (window.history.length > 1) { window.history.back(); return 'pop'; }" +
                    "  return 'exit';" +
                    "})()",
                    new BackResultCallback(this));
                return;
            }
        }
        catch { /* WebView missing or threw — fall through to default */ }
        base.OnBackPressed();
    }
#pragma warning restore CA1422

    /// <summary>Status-bar / nav-bar heights in CSS px, in a static so the
    /// JS string built in <see cref="SafeAreaCssSnippet"/> can be consumed
    /// by MauiProgram's BlazorWebViewInitialized hook (the only place the
    /// WebView JS context is guaranteed ready). Seeded from resource dimens
    /// in OnCreate, refined by the window-insets listener afterwards.</summary>
    public static float SafeTopCssPx { get; private set; }
    public static float SafeBottomCssPx { get; private set; }

    /// <summary>JS that sets the --animarr-safe-* CSS custom properties on
    /// &lt;html&gt; from the current static inset values. MauiProgram evals
    /// this once the WebView reports initialized; the activity also evals it
    /// on every insets change.</summary>
    public static string SafeAreaCssSnippet()
    {
        var top = SafeTopCssPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bot = SafeBottomCssPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Inject a <style> with EXPLICIT px values rather than relying on a
        // CSS custom property. Setting --animarr-safe-top late (after the
        // BlazorWebView mounts) did update the var — getComputedStyle showed
        // 44px — but the `padding: var(...) 16px 0` SHORTHAND on .d-mobile-nav
        // never recomputed (computed padding-top stayed 0px on this Chromium
        // WebView). A dedicated stylesheet with literal px sidesteps the
        // shorthand-var recompute quirk entirely and is trivially overridable
        // because it's the last <style> in <head>. Re-inject on a few timers
        // so it survives Blazor's first renders + a server/theme swap.
        var css =
            "@media (max-width:959px){" +
            "  .d-mobile-nav{padding-top:" + top + "px !important;height:calc(56px + " + top + "px) !important;}" +
            "  .animarr-main{padding-top:calc(56px + " + top + "px) !important;}" +
            "  .d-hero,.d-chero,.d-mdhero{margin-top:calc(-1 * (56px + " + top + "px)) !important;}" +
            "  .d-tabbar{padding-bottom:max(18px," + bot + "px) !important;}" +
            "}" +
            ".d-topbar{padding-top:" + top + "px !important;height:calc(60px + " + top + "px) !important;}" +
            // Drawer / panel headers that pin to the top also clear the notch.
            ".d-profile__head,.emd__header{padding-top:calc(20px + " + top + "px) !important;}";
        // JSON-encode the CSS string so quotes/newlines are safe inside the
        // injected JS literal.
        var json = System.Text.Json.JsonSerializer.Serialize(css);
        return
            "(function(){" +
            "  function apply(){" +
            "    var el=document.getElementById('animarr-safe-style');" +
            "    if(!el){el=document.createElement('style');el.id='animarr-safe-style';document.head.appendChild(el);}" +
            "    el.textContent=" + json + ";" +
            "    var d=document.documentElement;" +
            "    d.style.setProperty('--animarr-safe-top','" + top + "px');" +
            "    d.style.setProperty('--animarr-safe-bottom','" + bot + "px');" +
            "  }" +
            "  apply();" +
            "  setTimeout(apply,400);setTimeout(apply,1200);setTimeout(apply,2500);" +
            "})();";
    }

    /// <summary>Push the cached inset heights into the live WebView. Used by
    /// the insets listener for live updates; the first authoritative push
    /// happens from MauiProgram's Initialized hook.</summary>
    internal void PushSafeAreaVars()
    {
        try
        {
            var webView = FindWebView(Window?.DecorView);
            webView?.EvaluateJavascript(SafeAreaCssSnippet(), null);
        }
        catch { /* WebView not ready — Initialized hook will push later */ }
    }

    /// <summary>Receives window-insets updates and refreshes the static inset
    /// values + re-pushes them to the WebView. Returns the insets unconsumed
    /// so MAUI's own layout still sees them.</summary>
    private sealed class InsetsListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        private readonly MainActivity _activity;
        public InsetsListener(MainActivity activity) { _activity = activity; }

        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(
            global::Android.Views.View v, AndroidX.Core.View.WindowInsetsCompat insets)
        {
            try
            {
                var bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
                var density = _activity.Resources?.DisplayMetrics?.Density ?? 1f;
                if (density <= 0) density = 1f;
                // Only override the resource-dimen seed when the live inset is
                // larger (handles devices where the dimen under-reports).
                var topCss = bars.Top / density;
                var botCss = bars.Bottom / density;
                if (topCss > 0) SafeTopCssPx = topCss;
                if (botCss > 0) SafeBottomCssPx = botCss;
                _activity.PushSafeAreaVars();
            }
            catch { }
            return insets;
        }
    }

    private global::Android.Webkit.WebView? _cachedWebView;
    internal global::Android.Webkit.WebView? FindWebView(global::Android.Views.View? root)
    {
        if (_cachedWebView is not null) return _cachedWebView;
        if (root is null) return null;
        if (root is global::Android.Webkit.WebView wv) { _cachedWebView = wv; return wv; }
        if (root is global::Android.Views.ViewGroup vg)
        {
            for (int i = 0; i < vg.ChildCount; i++)
            {
                var found = FindWebView(vg.GetChildAt(i));
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// OnBackPressedDispatcher callback that runs on every Android version,
    /// including the predictive back gesture on Android 13+ (which doesn't
    /// fire the deprecated OnBackPressed override). When invoked:
    ///
    ///   • Asks the WebView whether window.history can be popped.
    ///   • If yes — Blazor router pops a route, we stay in the app.
    ///   • If no — `finish()` the activity so the launcher reclaims focus.
    ///
    /// The callback is permanently enabled (`true` to base()) so the system
    /// always calls us first; we decide whether to forward or consume.
    /// </summary>
    private sealed class AnimarrBackCallback : OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public AnimarrBackCallback(MainActivity activity) : base(enabled: true)
        {
            _activity = activity;
        }

        public override void HandleOnBackPressed()
        {
            try
            {
                var webView = _activity.FindWebView(_activity.Window?.DecorView);
                if (webView is not null)
                {
                    // Use ValueCallback so we can decide whether to finish()
                    // the activity AFTER the WebView reports its history
                    // length. The renderer runs JS on a separate thread; the
                    // callback below marshals back to the activity thread.
                    webView.EvaluateJavascript(
                        "(function(){var h=window.history.length;if(h>1){window.history.back();return 'pop';}return 'exit';})()",
                        new BackResultCallback(_activity));
                    return; // we'll finish() inside the callback if needed
                }
            }
            catch { /* fall through to immediate finish */ }
            _activity.Finish();
        }
    }

    /// <summary>ValueCallback receiver for the EvaluateJavascript above —
    /// reads the JS return value and decides whether to finish the
    /// activity. The string arrives wrapped in JSON quotes ("consumed"
    /// / "pop" / "exit") so we substring-match.
    ///
    /// • "consumed" — a drawer / player closed in JS, stay put.
    /// • "pop"      — Blazor router popped one route, stay put.
    /// • "exit"     — user was at Welcome with no history; finish() so
    ///                the launcher takes over instead of leaving the
    ///                app frozen on a dead screen.
    /// • anything else (transport blip, null) — treat as 'exit'
    ///   defensively, the user can always relaunch.</summary>
    private sealed class BackResultCallback : Java.Lang.Object, global::Android.Webkit.IValueCallback
    {
        private readonly MainActivity _activity;
        public BackResultCallback(MainActivity activity) { _activity = activity; }
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            var s = value?.ToString() ?? "";
            if (s.Contains("consumed", System.StringComparison.OrdinalIgnoreCase)) return;
            if (s.Contains("pop",      System.StringComparison.OrdinalIgnoreCase)) return;
            _activity.RunOnUiThread(() => _activity.Finish());
        }
    }

    // ── Phase 2d lifecycle hooks for native ExoPlayer ──────────────────
    // Without these the ExoPlayer instance keeps decoding in the background
    // after the user presses Home — burns battery, holds the audio focus,
    // and can crash the renderer on memory pressure. Web (BlazorWebView's
    // hls.js) doesn't have this issue because the WebView lifecycle
    // pauses its own decoders.
    protected override void OnPause()
    {
        base.OnPause();
        try { Services.NativePlayerService.Instance?.OnHostActivityPaused(); }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("Animarr.NativePlayer", $"OnHostActivityPaused threw: {ex.Message}");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        try { Services.NativePlayerService.Instance?.OnHostActivityResumed(); }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("Animarr.NativePlayer", $"OnHostActivityResumed threw: {ex.Message}");
        }
    }

    protected override void OnDestroy()
    {
        try { Services.NativePlayerService.Instance?.OnHostActivityDestroyed(); }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("Animarr.NativePlayer", $"OnHostActivityDestroyed threw: {ex.Message}");
        }
        base.OnDestroy();
    }

    /// <summary>
    /// Parse `animarr://play/{mediaId}` and stash the ID for Blazor to pick up.
    /// Anything else (the regular launcher intent, or http(s) Open-in
    /// targets) → no-op.
    /// </summary>
    private static void CaptureDeepLink(Intent? intent)
    {
        var data = intent?.Data;
        if (data is null) return;
        if (!string.Equals(data.Scheme, "animarr", System.StringComparison.OrdinalIgnoreCase)) return;
        // Path is "/{mediaId}" when host is "play"; first segment is the ID.
        var path = data.Path;
        if (string.IsNullOrEmpty(path)) return;
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var mediaId = slash >= 0 ? trimmed[..slash] : trimmed;
        if (!string.IsNullOrEmpty(mediaId)) PendingDeepLinkMediaId = mediaId;
    }
}
