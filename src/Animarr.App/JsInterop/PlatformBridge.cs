using Microsoft.JSInterop;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
#endif

namespace Animarr.App.JsInterop;

/// <summary>
/// Static JS-invokable surface for platform-level capabilities the WebView
/// can't probe on its own. Bonjour / mDNS scan lives in <see cref="MdnsBridge"/>;
/// this one's everything else — currently just "is this an Android TV?".
///
/// The JS side queries this once after Blazor mounts and applies the result
/// as a class on &lt;html&gt; so anything that wants TV-specific behaviour
/// (focus rings, larger hit targets, D-pad nav, hero pager variants) can opt
/// in via CSS. The viewport-fix script in index.html still runs its UA + size
/// heuristic synchronously at boot — this method only fills in the gaps where
/// the heuristic guessed wrong (Xiaomi Mi TV with non-standard UA, emulators,
/// budget OEM TVs).
/// </summary>
public static class PlatformBridge
{
    /// <summary>
    /// Authoritative TV check.
    ///
    /// On Android: <see cref="UiModeManager.CurrentModeType"/> is the canonical
    /// signal — that's how Android itself decides whether to bring up the
    /// Leanback launcher vs the phone launcher, so it never lies. The
    /// <c>FEATURE_LEANBACK</c> backstop catches emulators and OEM devices
    /// (Xiaomi MIUI for TV, etc.) where UiModeManager returns TypeNormal but
    /// the system still ships a TV launcher / leanback intent filter.
    ///
    /// On non-Android targets: always returns <c>false</c>. Windows MAUI runs
    /// on PCs and TV-mode CSS doesn't fit; iOS MAUI doesn't target tvOS in
    /// this build (would need a separate net10.0-tvos TFM and Storyboard
    /// rework to ship there).
    /// </summary>
    [JSInvokable("IsTelevision")]
    public static bool IsTelevision()
    {
#if ANDROID
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            if (ctx is null) return false;

            if (ctx.GetSystemService(Context.UiModeService) is UiModeManager ui &&
                ui.CurrentModeType == UiMode.TypeTelevision)
                return true;

            if (ctx.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) == true)
                return true;
        }
        catch
        {
            // Anything throws (rare — could happen if AppContext isn't ready
            // yet during very early boot) → return false so the JS-side
            // heuristic in index.html stays authoritative for this paint.
        }
        return false;
#else
        return false;
#endif
    }

    /// <summary>
    /// Drain any pending deep-link target captured by <see cref="MainActivity"/>'s
    /// Intent handling (Google TV's Continue Watching tile tap fires an
    /// <c>animarr://play/{mediaId}</c> Intent.ActionView at us). Returns the
    /// stashed media ID and clears the pending slot in one call, so repeated
    /// invocations from the same Intent only resolve once.
    ///
    /// On non-Android targets always null — Windows / macOS WebView don't have
    /// a launcher carousel + deep link to dispatch to us.
    ///
    /// Consumed by App.razor / Routes.razor on Blazor mount: when this
    /// returns a non-null value we <c>NavigationManager.NavigateTo</c> to
    /// the media detail page so the user lands where they tapped, not on
    /// /welcome.
    /// </summary>
    [JSInvokable("ConsumePendingDeepLink")]
    public static string? ConsumePendingDeepLink()
    {
#if ANDROID
        return MainActivity.DrainPendingDeepLink();
#else
        return null;
#endif
    }

    /// <summary>Fetch an http:// image from .NET land and return it as a
    /// base64 data-URL. The JS image-proxy uses this to side-step Chromium
    /// WebView's mixed-content gate, which refuses to load http:// resources
    /// from the https://0.0.0.x/ bundle even with MixedContentMode set to
    /// AlwaysAllow. Returns the data URL (e.g. "data:image/jpeg;base64,…")
    /// or an empty string on failure — JS treats empty as "leave img.src
    /// alone" so the broken-image icon still appears for genuinely missing
    /// resources rather than silently disappearing.</summary>
    [JSInvokable("FetchImageAsDataUrl")]
    public static async Task<string> FetchImageAsDataUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = System.TimeSpan.FromSeconds(8),
            };
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return "";
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var mime  = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return $"data:{mime};base64,{System.Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Ask the OS for the CAMERA runtime permission (if not already
    /// held) and return whether we have it after the prompt. The phone's QR
    /// scanner in <c>PairConfirm.razor</c> calls this BEFORE invoking the
    /// html5-qrcode bridge — without an explicit grant Android's WebView
    /// resolves <c>onPermissionRequest</c> for video-capture as Deny and
    /// the JS side sees "NotAllowedError".
    ///
    /// On non-Android targets always returns true so the QR scanner code
    /// path is uniform; WASM in a browser already prompts via the standard
    /// getUserMedia dialog and doesn't need this round trip.</summary>
    [JSInvokable("RequestCameraPermission")]
    public static async Task<bool> RequestCameraPermissionAsync()
    {
#if ANDROID
        try
        {
            var status = await Microsoft.Maui.ApplicationModel.Permissions
                .CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.Camera>();
            if (status == Microsoft.Maui.ApplicationModel.PermissionStatus.Granted) return true;

            status = await Microsoft.Maui.ApplicationModel.Permissions
                .RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.Camera>();
            return status == Microsoft.Maui.ApplicationModel.PermissionStatus.Granted;
        }
        catch
        {
            return false;
        }
#else
        await Task.CompletedTask;
        return true;
#endif
    }
}
