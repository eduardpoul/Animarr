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
        AcquireMulticastLock();
        base.OnCreate();
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
