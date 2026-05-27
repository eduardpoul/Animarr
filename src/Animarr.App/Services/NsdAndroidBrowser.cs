#if ANDROID
using Android.App;
using Android.Content;
using Android.Net.Nsd;
using Android.OS;

namespace Animarr.App.Services;

/// <summary>
/// Android-specific mDNS browser that delegates to the system <c>NsdManager</c>
/// (Network Service Discovery). On Android, going through NSD is the only
/// reliable way to receive <c>_animarr._tcp.local</c> announcements:
///
///   • Userspace UDP 5353 listeners (Makaretu) often miss multicast packets
///     even with the multicast lock held because the system <c>mdnsd</c>
///     daemon owns the socket and the kernel routes packets through it.
///   • NSD subscribes through that same daemon, so we get the packets the OS
///     already received — no socket binds, no IGMP join races, no permission
///     surprises on locked-down builds.
///
/// We surface discovered services through the same <see cref="DiscoveredServer"/>
/// shape <see cref="MdnsBrowserService"/> uses elsewhere so callers don't
/// need to know which back-end produced the entry.
///
/// Lifecycle: instantiated once at startup, browsing is started immediately
/// and runs for the process lifetime. Repeated <see cref="Restart"/> calls
/// rebuild the listener (NsdManager refuses to register the same listener
/// twice, so re-issuing a "scan" means stop → start).
/// </summary>
internal sealed class NsdAndroidBrowser : Java.Lang.Object,
    NsdManager.IDiscoveryListener
{
    private const string ServiceType = "_animarr._tcp.";

    private readonly NsdManager? _nsd;
    private readonly Action<DiscoveredServer> _onFound;
    private readonly System.Action<string> _diag;
    private bool _browsing;

    public NsdAndroidBrowser(Context context, Action<DiscoveredServer> onFound, System.Action<string> diag)
    {
        _onFound = onFound;
        _diag    = diag;
        _nsd     = (NsdManager?)context.GetSystemService(Context.NsdService);
    }

    public void Start()
    {
        if (_nsd is null)
        {
            _diag("NSD: NsdManager unavailable.");
            return;
        }
        if (_browsing) return;
        try
        {
            _nsd.DiscoverServices(ServiceType, NsdProtocol.DnsSd, this);
            _browsing = true;
            _diag($"NSD: discoverServices({ServiceType}) issued.");
        }
        catch (System.Exception ex)
        {
            _diag($"NSD: discoverServices failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>NSD throws if you call DiscoverServices twice with the same
    /// listener. To re-issue a fresh scan we tear down and restart.</summary>
    public void Restart()
    {
        if (_nsd is null) return;
        try
        {
            if (_browsing)
            {
                _nsd.StopServiceDiscovery(this);
                _browsing = false;
            }
        }
        catch (System.Exception ex)
        {
            _diag($"NSD: stopServiceDiscovery failed: {ex.Message}");
        }
        Start();
    }

    // --- IDiscoveryListener ---

    public void OnDiscoveryStarted(string? serviceType)     => _diag($"NSD: discovery started for {serviceType}.");
    public void OnDiscoveryStopped(string? serviceType)
    {
        _browsing = false;
        _diag($"NSD: discovery stopped for {serviceType}.");
    }
    public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) =>
        _diag($"NSD: start discovery failed type={serviceType} code={errorCode}");
    public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) =>
        _diag($"NSD: stop discovery failed type={serviceType} code={errorCode}");

    public void OnServiceFound(NsdServiceInfo? info)
    {
        if (info is null) return;
        _diag($"NSD: service FOUND name='{info.ServiceName}' type='{info.ServiceType}'");
        // ServiceType is reported back as the resolved label form. We sent
        // "_animarr._tcp." so we accept anything that contains "_animarr".
        if (info.ServiceType is null || !info.ServiceType.Contains("_animarr", System.StringComparison.OrdinalIgnoreCase))
            return;
        // Each resolveService call needs its OWN listener instance — NSD
        // throws "listener already in use" if you reuse one across resolves
        // (even sequentially), so we spawn a fresh per-service listener that
        // routes back through OnResolved + ReleaseResolveSlot.
        EnqueueResolve(info);
    }

    public void OnServiceLost(NsdServiceInfo? info) =>
        _diag($"NSD: service LOST name='{info?.ServiceName}'");

    // --- Resolve queue ---
    //
    // NsdManager.resolveService is single-shot per listener instance: calling
    // it again on the same listener (even after a completed resolve) throws
    // "listener already in use". We sidestep by spawning a fresh listener for
    // each resolve, but we still serialise them through a busy flag because
    // some Android versions get confused when several resolves are in flight
    // at once.

    private readonly System.Collections.Concurrent.ConcurrentQueue<NsdServiceInfo> _pendingResolve = new();
    private int _resolveBusy; // 0 = idle, 1 = resolve in flight

    private void EnqueueResolve(NsdServiceInfo info)
    {
        _pendingResolve.Enqueue(info);
        TryStartNextResolve();
    }

    private void TryStartNextResolve()
    {
        if (_nsd is null) return;
        if (System.Threading.Interlocked.CompareExchange(ref _resolveBusy, 1, 0) != 0) return;
        if (!_pendingResolve.TryDequeue(out var info))
        {
            System.Threading.Interlocked.Exchange(ref _resolveBusy, 0);
            return;
        }
        try
        {
            var listener = new ResolveListener(this, info.ServiceName ?? "");
            _nsd.ResolveService(info, listener);
        }
        catch (System.Exception ex)
        {
            _diag($"NSD: resolveService threw: {ex.Message}");
            ReleaseResolveSlot();
        }
    }

    private void ReleaseResolveSlot()
    {
        System.Threading.Interlocked.Exchange(ref _resolveBusy, 0);
        TryStartNextResolve();
    }

    /// <summary>Called by <see cref="ResolveListener"/> when a service has
    /// host/port/TXT resolved. Decodes the TXT records and pushes a
    /// <see cref="DiscoveredServer"/> to the outer service via _onFound.</summary>
    private void OnResolved(NsdServiceInfo info)
    {
        try
        {
            var host = info.Host?.HostAddress;
            if (string.IsNullOrEmpty(host))
            {
                _diag($"NSD: resolved {info.ServiceName} but no Host — skipping.");
                return;
            }
            var port = info.Port > 0 ? info.Port : 8080;

            var attrs = info.Attributes;
            string serverId = "", version = "", nameProp = "";
            if (attrs is not null)
            {
                foreach (var kvp in attrs)
                {
                    var value = kvp.Value is null ? "" : System.Text.Encoding.UTF8.GetString(kvp.Value);
                    switch (kvp.Key)
                    {
                        case "serverId": serverId = value; break;
                        case "version":  version  = value; break;
                        case "name":     nameProp = value; break;
                    }
                }
            }

            string displayName = !string.IsNullOrWhiteSpace(nameProp)
                ? nameProp
                : (info.ServiceName ?? "Animarr");

            if (host.Contains(':')) host = $"[{host}]";
            var baseUrl = $"http://{host}:{port}";

            _diag($"NSD: resolved {displayName} baseUrl={baseUrl} serverId={serverId} version={version}");
            _onFound(new DiscoveredServer(displayName, baseUrl, serverId, version));
        }
        finally
        {
            ReleaseResolveSlot();
        }
    }

    private void OnResolveFailed(string name, NsdFailure errorCode)
    {
        _diag($"NSD: resolve failed name='{name}' code={errorCode}");
        ReleaseResolveSlot();
    }

    /// <summary>Per-resolve listener — created fresh for each
    /// <c>ResolveService</c> call because NsdManager forbids listener reuse.
    /// Routes results back through the parent browser's success/failure
    /// handlers so the resolve queue can advance.</summary>
    private sealed class ResolveListener : Java.Lang.Object, NsdManager.IResolveListener
    {
        private readonly NsdAndroidBrowser _parent;
        private readonly string _name;

        public ResolveListener(NsdAndroidBrowser parent, string name)
        {
            _parent = parent;
            _name   = name;
        }

        public void OnResolveFailed(NsdServiceInfo? info, NsdFailure errorCode) =>
            _parent.OnResolveFailed(info?.ServiceName ?? _name, errorCode);

        public void OnServiceResolved(NsdServiceInfo? info)
        {
            if (info is null) { _parent.OnResolveFailed(_name, NsdFailure.InternalError); return; }
            _parent.OnResolved(info);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (_browsing && _nsd is not null) _nsd.StopServiceDiscovery(this); } catch { }
        }
        base.Dispose(disposing);
    }
}
#endif
