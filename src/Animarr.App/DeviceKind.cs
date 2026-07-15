namespace Animarr.App;

/// <summary>
/// Cheap host-class detection used to pick the right shell at launch. The native
/// CollectionView UI is a 10-foot / D-pad optimisation that only makes sense on
/// a TV; phones and tablets keep the mature, responsive Blazor app (touch-first,
/// every screen). One APK, two front-ends, chosen here.
/// </summary>
public static class DeviceKind
{
#if ANDROID
    private static bool? _isTv;

    /// <summary>
    /// True on Android TV. Detected via the Leanback system feature (what the
    /// Play Store + launchers use to classify a device as a TV) with a UI-mode
    /// fallback (<c>UI_MODE_TYPE_TELEVISION</c>) for boxes that report the mode
    /// but not the feature. Cached — the answer can't change within a process.
    /// </summary>
    public static bool IsTv
    {
        get
        {
            if (_isTv is bool cached) return cached;
            bool tv = false;
            try
            {
                var ctx = Android.App.Application.Context;
                var pm  = ctx.PackageManager;
                if (pm?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureLeanback) == true ||
                    pm?.HasSystemFeature("android.hardware.type.television") == true)
                {
                    tv = true;
                }
                else if (ctx.GetSystemService(Android.Content.Context.UiModeService)
                             is Android.App.UiModeManager um &&
                         um.CurrentModeType == Android.Content.Res.UiMode.TypeTelevision)
                {
                    tv = true;
                }
            }
            catch { /* be conservative: unknown → treat as non-TV (phone shell) */ }
            _isTv = tv;
            return tv;
        }
    }
#else
    /// <summary>Non-Android hosts (iOS, Mac, Windows) are never TVs here.</summary>
    public static bool IsTv => false;
#endif
}
