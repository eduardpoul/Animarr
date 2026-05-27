using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

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
            }
        }
        catch
        {
            // OEM ROMs (Mi TV, Huawei) sometimes reject SetDecorFitsSystemWindows
            // on older API levels — fall back to the default layout, the UI
            // still works just with a ~24px black strip at the top.
        }

        CaptureDeepLink(Intent);

        // Phase 2b (2026-05-27): native ExoPlayer video surface.
        // We insert a TextureView at the very bottom of the activity's view
        // tree (index 0 of DecorView). When the player opens, ExoPlayer hands
        // its frames to this TextureView; the BlazorWebView above has a
        // transparent background (set in MauiProgram's WebView mapper) so the
        // video shows through wherever the HTML body is also transparent.
        // Outside playback the TextureView is GONE — zero compositing cost.
        // TextureView (not SurfaceView) so the regular GL pipeline can
        // composite it BEHIND the translucent WebView; SurfaceView's
        // hole-punch model wouldn't let the HUD overlay layer cleanly.
        try
        {
            if (Window?.DecorView is ViewGroup decor)
            {
                var tv = new TextureView(this)
                {
                    Visibility = ViewStates.Gone,
                    LayoutParameters = new ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent,
                        ViewGroup.LayoutParams.MatchParent),
                };
                decor.AddView(tv, 0);
                Services.NativePlayerService.RegisterTextureView(tv);
            }
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Error("Animarr.NativePlayer",
                $"Failed to insert TextureView: {ex.Message}");
        }
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
