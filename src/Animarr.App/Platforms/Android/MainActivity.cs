using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

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
        base.OnCreate(savedInstanceState);
        // Cold start launched via deep link — Intent's already on us.
        CaptureDeepLink(Intent);
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
