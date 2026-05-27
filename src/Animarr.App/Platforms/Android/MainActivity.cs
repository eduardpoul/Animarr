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
public class MainActivity : MauiAppCompatActivity
{
}
