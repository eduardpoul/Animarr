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
    // Phone: the native player goes picture-in-picture on Home / its PiP
    // button (PlayerPage). The config changes below keep the activity alive
    // through the PiP resize.
    SupportsPictureInPicture = true,
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

    /// <summary>
    /// TV interface-scale setting (Маленький / Обычный / Большой in the profile
    /// panel). Applied by overriding the activity context's DENSITY — dp sizes
    /// resolve against it, so cards, posters, paddings AND text all zoom
    /// together like a display-scale setting (a fontScale-only override was
    /// tried first and read as "nothing changed": text is a small fraction of
    /// the screen). Gated to Leanback so the phone/tablet Blazor path is
    /// untouched. Stored in plain Android SharedPreferences (not MAUI
    /// Preferences) because this runs before MAUI Essentials initializes.
    /// </summary>
    internal const string TvPrefsName   = "animarr_tv";
    internal const string UiScalePref   = "ui_scale";

    /// <summary>Wrap <paramref name="ctx"/> with the scaled-density config, or
    /// return it untouched. Called from BOTH MainApplication and MainActivity
    /// AttachBaseContext: MAUI converts dp→px through the APPLICATION context's
    /// density while native text resolves against the activity's — overriding
    /// only one of them scales half the UI.</summary>
    internal static Context? ApplyTvUiScale(Context? ctx)
    {
        try
        {
            if (ctx?.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) != true)
                return ctx;
            var sp = ctx.GetSharedPreferences(TvPrefsName, FileCreationMode.Private);
            var scale = sp?.GetFloat(UiScalePref, 1f) ?? 1f;
            if (Math.Abs(scale - 1f) <= 0.01f || ctx.Resources?.Configuration is not { } baseCfg)
                return ctx;
            var cfg = new Android.Content.Res.Configuration(baseCfg);
            // Density drives BOTH dp and sp, so fontScale stays as-is —
            // multiplying both would double-zoom the text.
            var baseDpi = baseCfg.DensityDpi > 0
                ? baseCfg.DensityDpi
                : (int)Android.Util.DisplayMetricsDensity.Xhigh;
            cfg.DensityDpi = (int)Math.Round(baseDpi * scale);
            return ctx.CreateConfigurationContext(cfg) ?? ctx;
        }
        catch { return ctx; /* bad prefs / OEM quirks — unscaled context */ }
    }

    protected override void AttachBaseContext(Context? @base)
        => base.AttachBaseContext(ApplyTvUiScale(@base));

    /// <summary>Mutate the APPLICATION-context Resources to the scaled density
    /// (deprecated UpdateConfiguration — the only way to rescale an existing
    /// Resources in place). MAUI's dp→px conversions read this context, while
    /// native text resolves against the activity's — both must agree. Re-applied
    /// from several hooks because the system re-pushes the pristine config.</summary>
    private static int s_baseAppDpi;

    internal static void ApplyTvUiScaleToAppResources()
    {
        try
        {
            var ctx = Android.App.Application.Context;
            if (ctx.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) != true)
                return;
            var sp = ctx.GetSharedPreferences(TvPrefsName, FileCreationMode.Private);
            var scale = sp?.GetFloat(UiScalePref, 1f) ?? 1f;
            // NB: no early return for scale==1 — switching back to "Обычный"
            // must RESET an already-scaled config to the captured base dpi.

            var res = ctx.Resources;
            if (res?.Configuration is not { } baseCfg || res.DisplayMetrics is null) return;
            // Capture the device's real dpi on the first (pristine) call —
            // re-applies must scale from THAT, not from an already-scaled value.
            if (s_baseAppDpi == 0)
                s_baseAppDpi = baseCfg.DensityDpi > 0
                    ? baseCfg.DensityDpi
                    : (int)Android.Util.DisplayMetricsDensity.Xhigh;
            var targetDpi = (int)Math.Round(s_baseAppDpi * scale);
            if (baseCfg.DensityDpi == targetDpi) return;   // already scaled

            var cfg = new Android.Content.Res.Configuration(baseCfg) { DensityDpi = targetDpi };
#pragma warning disable CA1422
            res.UpdateConfiguration(cfg, res.DisplayMetrics);
#pragma warning restore CA1422
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("Animarr.UiScale", $"app-resources scale failed: {ex.Message}");
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // The system re-pushed the pristine app configuration while binding this
        // activity — re-apply the TV scale BEFORE MAUI initializes and reads
        // density for its dp→px conversions.
        ApplyTvUiScaleToAppResources();
        Bench.BenchLog.Mark("activity-oncreate-start");

        // MulticastLock is acquired earlier in MainApplication.OnCreate so it's
        // already held by the time MauiProgram.CreateMauiApp resolves
        // MdnsBrowserService. Activity.OnCreate is too late for that.
        base.OnCreate(savedInstanceState);

        // The WebView and its parents are transparent (native-video
        // passthrough), so until index.html paints, the window background
        // shows — the light theme's white flashed between the splash and the
        // boot screen. Paint it the boot screen's colour instead.
        Window?.SetBackgroundDrawable(
            new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.ParseColor("#0b0d10")));

        // UI-scale diagnostics: which density each context actually reports.
        try
        {
            Android.Util.Log.Info("Animarr.UiScale",
                $"activity density={Resources?.DisplayMetrics?.Density} " +
                $"app density={Android.App.Application.Context.Resources?.DisplayMetrics?.Density}");
        }
        catch { }

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
        Bench.BenchLog.Mode = Intent?.GetStringExtra("bench");
        Bench.BenchLog.Mark($"activity-oncreate-end mode={Bench.BenchLog.Mode}");

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

        // The native ExoPlayer's video surface is a SurfaceView the native
        // PlayerPage adds under its XAML HUD (NativePlayerService.
        // RegisterSurfaceView); the activity no longer inserts one.
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
    // Every key passes here first: the native TV screens need to know whether a
    // focus change came from an arrow press ("select on focus" tabs / pills).
    public override bool DispatchKeyEvent(Android.Views.KeyEvent? e)
    {
        if (e is not null) Tv.TvInput.OnKey(e);
        return base.DispatchKeyEvent(e);
    }

    public override bool OnKeyDown(Android.Views.Keycode keyCode, Android.Views.KeyEvent? e)
    {
        if (PlayerPage.HandleGlobalKey((int)keyCode)) return true;
        // The native PIN keypad takes the remote's number keys (0-9, DEL).
        if (TvPinPage.HandleGlobalKey((int)keyCode)) return true;
        return base.OnKeyDown(keyCode, e);
    }

#pragma warning disable CA1422 // OnBackPressed is deprecated on API 33+ but still fires
    public override void OnBackPressed()
    {
        if (TryHandleWebViewBack()) return;
        base.OnBackPressed();
    }
#pragma warning restore CA1422

    /// <summary>Back decision for the phone's WebView shell. In JS: close the
    /// topmost drawer / modal / player; else pop one route; else, off the root,
    /// go Home (a deep link has no history); on Home itself send the app to the
    /// background. Shared by <see cref="OnBackPressed"/> and
    /// <c>MainPage.OnBackButtonPressed</c> — on Android 13+ Back reaches MAUI's
    /// page hook, not the activity, and MAUI's default then moved the app to
    /// the background, so Back from Settings looked like the app closing.
    ///
    /// The WebView can also step back through its own history on the same
    /// press, before this script runs (seen on API 36): the page is already
    /// Home by then. index.html stamps every popstate, and a fresh stamp
    /// means the press was handled — deciding again would exit the app from
    /// a Back that only meant "leave Settings".</summary>
    internal bool TryHandleWebViewBack()
    {
        // Player HUD open → Back closes the HUD, not the page.
        if (PlayerPage.HandleGlobalBack()) return true;
        // New native TV screens: Back first returns focus to the top menu.
        if (Tv.TvBack.Handler?.Invoke() == true) return true;

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
                return true;
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
                    "  if (window.__animarrPoppedAt && performance.now() - window.__animarrPoppedAt < 400) return 'pop';" +
                    "  if (window.animarrHandleBack && window.animarrHandleBack()) return 'consumed';" +
                    "  var p = location.pathname;" +
                    "  if (p === '/' || p === '/welcome') return 'exit';" +
                    "  var can = window.navigation ? window.navigation.canGoBack : window.history.length > 1;" +
                    "  if (can) { window.history.back(); return 'pop'; }" +
                    "  if (window.Blazor && window.Blazor.navigateTo) { window.Blazor.navigateTo('/'); return 'home'; }" +
                    "  return 'exit';" +
                    "})()",
                    new BackResultCallback(this));
                return true;
            }
        }
        catch { /* WebView missing or threw — fall through to default */ }
        return false;
    }

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
        // The first push can land while index.html is still parsing — no
        // <head> yet. apply() threw on it and, the retries being scheduled
        // after that call, never ran: some launches kept no safe-area at all
        // (top bar under the status bar, tab bar under the gesture bar). The
        // timers go first now and apply() waits for <head>.
        return
            "(function(){" +
            "  function apply(){" +
            "    if(!document.head||!document.documentElement)return;" +
            "    var el=document.getElementById('animarr-safe-style');" +
            "    if(!el){el=document.createElement('style');el.id='animarr-safe-style';document.head.appendChild(el);}" +
            "    el.textContent=" + json + ";" +
            "    var d=document.documentElement;" +
            "    d.style.setProperty('--animarr-safe-top','" + top + "px');" +
            "    d.style.setProperty('--animarr-safe-bottom','" + bot + "px');" +
            "  }" +
            "  setTimeout(apply,400);setTimeout(apply,1200);setTimeout(apply,2500);" +
            "  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',apply);" +
            "  apply();" +
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
    /// • "home"     — no history off the root (deep link): went Home.
    /// • "exit"     — user is on Home / Welcome: move the task to the
    ///                background like the launcher's own Back. The activity
    ///                stays alive, so reopening resumes where the user was
    ///                instead of rebooting the WebView and reloading it all.
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
            if (s.Contains("home",     System.StringComparison.OrdinalIgnoreCase)) return;
            _activity.RunOnUiThread(() => _activity.MoveTaskToBack(true));
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
        // In picture-in-picture the activity is paused but on screen: playback goes on.
        if (IsInPictureInPictureMode) return;
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

    /// <summary>No longer visible — also the end of a picture-in-picture
    /// window the user closed (OnPause didn't pause it): pause.</summary>
    protected override void OnStop()
    {
        base.OnStop();
        try { Services.NativePlayerService.Instance?.OnHostActivityStopped(); }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("Animarr.NativePlayer", $"OnHostActivityStopped threw: {ex.Message}");
        }
    }

    /// <summary>Home / Recents while the phone's native player plays: below
    /// Android 12 PiP is entered here (12+ auto-enters from the player's
    /// PictureInPictureParams).</summary>
    protected override void OnUserLeaveHint()
    {
        base.OnUserLeaveHint();
        PlayerPage.OnUserLeaveHintGlobal();
    }

    public override void OnPictureInPictureModeChanged(bool isInPictureInPictureMode,
                                                       Android.Content.Res.Configuration? newConfig)
    {
        base.OnPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        PlayerPage.OnPipModeChangedGlobal(isInPictureInPictureMode);
    }

    /// <summary>An external player (VLC / mpv / MX) returned with the position it stopped at.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (Services.ExternalPlayers.HandleResult(requestCode, resultCode, data)) return;
        base.OnActivityResult(requestCode, resultCode, data);
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
        // animarr://play/{mediaId} only, the id a GUID: any app can send this
        // intent, and the id becomes a route ("/media/{id}") in the WebView.
        if (!string.Equals(data.Host, "play", System.StringComparison.OrdinalIgnoreCase)) return;
        var path = data.Path;
        if (string.IsNullOrEmpty(path)) return;
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var mediaId = slash >= 0 ? trimmed[..slash] : trimmed;
        if (System.Guid.TryParse(mediaId, out var id)) PendingDeepLinkMediaId = id.ToString();
    }
}
