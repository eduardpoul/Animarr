using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.Runtime;

namespace Animarr.App;

[Application]
public class MainApplication : MauiApplication
{
    /// <summary>WiFi multicast lock — held for the lifetime of the process so
    /// Android's WiFi driver doesn't filter UDP 5353 mDNS traffic. Must be
    /// acquired BEFORE <see cref="MauiProgram.CreateMauiApp"/> runs, because
    /// CreateMauiApp eagerly resolves <c>MdnsBrowserService</c> which binds the
    /// multicast socket synchronously in its constructor — moving the lock
    /// acquisition to MainActivity.OnCreate is too late (Activity.OnCreate
    /// runs AFTER Application.OnCreate, and the browser is already running by
    /// then). Without this, the kernel rejects the join request and no
    /// announcements ever reach Makaretu's listener for the current process
    /// boot.</summary>
    private WifiManager.MulticastLock? _multicastLock;

    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        // TV interface scale: MAUI's dp→px conversions read density from the
        // APPLICATION context, so it must be scaled here too (the activity-level
        // override alone only scales text). NB: Application.AttachBaseContext
        // cannot be overridden in C# — it runs before the .NET runtime loads
        // (UnsatisfiedLinkError) — so the app Resources are mutated in OnCreate
        // instead. The system re-pushes the pristine configuration when the
        // activity spins up, so MainActivity.OnCreate and OnConfigurationChanged
        // re-apply the same mutation.
        MainActivity.ApplyTvUiScaleToAppResources();
        AcquireMulticastLock();
        base.OnCreate();
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        // The system just replaced the app configuration — re-apply the scale.
        MainActivity.ApplyTvUiScaleToAppResources();
    }

    private void AcquireMulticastLock()
    {
        try
        {
            var wifi = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
            if (wifi is null)
            {
                Android.Util.Log.Warn("Animarr.mDNS", "WifiManager unavailable in Application — multicast lock skipped.");
                return;
            }
            _multicastLock = wifi.CreateMulticastLock("animarr-mdns");
            _multicastLock.SetReferenceCounted(false);
            _multicastLock.Acquire();
            Android.Util.Log.Info("Animarr.mDNS",
                $"Application: multicast lock acquired (held={_multicastLock.IsHeld}).");
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Error("Animarr.mDNS", $"Application: multicast lock failed: {ex.Message}");
        }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
