using System.Collections.Concurrent;
using System.Net;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Animarr.App.Services;

/// <summary>
/// Browse-side counterpart to <c>Animarr.Web.Services.MdnsPublisherService</c>.
///
/// Listens for <c>_animarr._tcp.local.</c> instance announcements on the LAN
/// and surfaces them to the Blazor UI through <see cref="BrowseAsync"/>.
///
/// Platform back-ends:
///
///   • Android — uses the system <c>NsdManager</c> (Network Service Discovery)
///     via <c>NsdAndroidBrowser</c>. Going through the OS daemon is the only
///     reliable path on Android; userspace UDP 5353 listeners (Makaretu) often
///     never receive multicast packets even with the multicast lock held
///     because <c>mdnsd</c> owns the kernel-side socket.
///   • Windows / macOS / iOS / Catalyst — uses Makaretu.Dns.Multicast directly.
///     These platforms either don't have an OS-level mDNS daemon claiming
///     port 5353 (Windows) or expose APIs that don't reach raw TXT/SRV records
///     (iOS NetService), so Makaretu's userspace listener is the better fit.
///
/// Pure browser WASM can't open a UDP 5353 socket and never lands here — the
/// Discovery page falls back to manual URL entry in that case.
///
/// Lifecycle: singleton in MauiProgram.cs. The underlying transport
/// (NsdManager listener or MulticastService) lives for the whole process. We
/// also stash the instance in a static field (<see cref="Instance"/>) so the
/// [JSInvokable] dispatch in <c>MdnsBridge</c> can find us without juggling a
/// DotNetObjectReference per page nav.
/// </summary>
public sealed class MdnsBrowserService : IAsyncDisposable
{
    private const string ServiceType = "_animarr._tcp";

    private readonly ILogger<MdnsBrowserService> _log;
    private readonly ConcurrentDictionary<string, DiscoveredServer> _seen = new();

#if ANDROID
    private readonly NsdAndroidBrowser? _nsd;
#else
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
#endif

    /// <summary>Process-wide accessor for the singleton instance — set by
    /// MauiProgram once the service container is built so the static
    /// [JSInvokable] in MdnsBridge can reach the browser without juggling a
    /// DotNetObjectReference per page nav.</summary>
    public static MdnsBrowserService? Instance { get; private set; }

    public IReadOnlyCollection<DiscoveredServer> Servers => _seen.Values.ToArray();

    public MdnsBrowserService(ILogger<MdnsBrowserService> log)
    {
        _log = log;
        DiagLog("ctor: starting MdnsBrowserService.");

#if ANDROID
        try
        {
            var ctx = Android.App.Application.Context;
            _nsd = new NsdAndroidBrowser(ctx, OnNsdFound, DiagLog);
            _nsd.Start();
            DiagLog("Android NSD browse started.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NSD browse start failed.");
            DiagLog($"NSD start FAILED: {ex.GetType().Name}: {ex.Message}");
        }
#else
        _mdns = new MulticastService();
        _sd   = new ServiceDiscovery(_mdns);
        _sd.ServiceInstanceDiscovered += OnInstance;

        try
        {
            _mdns.Start();
            _sd.QueryServiceInstances(ServiceType);
            _log.LogInformation("mDNS browse started for {Service}.local.", ServiceType);
        }
        catch (Exception ex)
        {
            // Same soft-failure posture as the publisher service — broadcast
            // sockets can fail on tightly-locked-down corporate WiFi, in
            // emulators without the right NIC adapter, or behind certain VPNs.
            // The UI manual-add path still works in any of those cases.
            _log.LogWarning(ex, "mDNS browse start failed.");
        }
#endif
    }

    /// <summary>Writes to logcat under tag "Animarr.mDNS" on Android, no-op
    /// elsewhere. Plain ILogger output doesn't always surface in <c>adb logcat</c>
    /// on a release-mode MAUI build, which masks startup failures of the
    /// multicast service. This goes through <c>Android.Util.Log</c> directly so
    /// the trail is visible from a phone connected via ADB.</summary>
    private static void DiagLog(string msg)
    {
#if ANDROID
        try { Android.Util.Log.Info("Animarr.mDNS", msg); } catch { }
#endif
    }

    /// <summary>Static initializer — call from MauiProgram after the
    /// container is built. Idempotent.</summary>
    public static void RegisterStaticInstance(MdnsBrowserService svc) => Instance = svc;

    /// <summary>Re-issue a fresh query and wait briefly so newly woken-up
    /// servers have a chance to answer. Returns the dedup'd snapshot of
    /// everything we've seen — including stale entries from earlier in the
    /// session, since the publisher's reannounce TTL might leave them
    /// unrefreshed even when they're still reachable.</summary>
    public async Task<DiscoveredServer[]> BrowseAsync(int timeoutMs = 8000)
    {
        DiagLog($"BrowseAsync: snapshotting {ServiceType}.local (seen so far: {_seen.Count}, waiting up to {timeoutMs}ms).");
#if ANDROID
        // Don't restart NSD here — the system NsdManager is event-driven and
        // has been actively querying since process start. Calling Restart()
        // causes StopServiceDiscovery → DiscoverServices in quick succession,
        // and Stop is asynchronous: Start fires before Stop has freed the
        // listener slot, then fails with "listener already in use" and tears
        // down the entire browse. That used to make the first scan after a
        // Discovery-page mount return empty even though the server was
        // visible to NSD a few seconds later. Just wait + poll the cache.
#else
        try
        {
            _sd.QueryServiceInstances(ServiceType);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS re-query failed.");
        }
#endif

        // Poll the cache so we can short-circuit as soon as the first server
        // shows up — no point waiting the full 8s when a name was resolved
        // after 200ms. We still cap at timeoutMs so a network with zero
        // mDNS reachability falls through to the subnet probe quickly.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_seen.Count > 0)
            {
                // Give late responders a 400ms grace window so two adjacent
                // servers both make it into the UI in the same scan.
                await Task.Delay(400).ConfigureAwait(false);
                break;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
        DiagLog($"BrowseAsync: returning {_seen.Count} server(s).");
        return _seen.Values.ToArray();
    }

#if ANDROID
    private void OnNsdFound(DiscoveredServer server)
    {
        var key = !string.IsNullOrWhiteSpace(server.ServerId) ? server.ServerId : server.BaseUrl;
        _seen[key] = server;
        _log.LogDebug("NSD discovered: {Name} {BaseUrl}", server.Name, server.BaseUrl);
    }
#else

    private void OnInstance(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            var msg     = e.Message;
            var instLbl = e.ServiceInstanceName.ToString();

            // Pull TXT / SRV / A / AAAA records out of the additional + answer
            // sections. mDNS responders typically pack the full set into one
            // datagram so the client doesn't have to chase records, but we
            // tolerate any subset and just skip rendering when essentials are
            // missing.
            var allRecords = msg.Answers
                .Concat(msg.AdditionalRecords)
                .ToList();

            var txt = allRecords.OfType<TXTRecord>().FirstOrDefault();
            var srv = allRecords.OfType<SRVRecord>().FirstOrDefault();
            var a4  = allRecords.OfType<ARecord>().FirstOrDefault();
            var a6  = allRecords.OfType<AAAARecord>().FirstOrDefault();

            string serverId  = "";
            string version   = "";
            string nameProp  = "";
            if (txt is not null)
            {
                foreach (var line in txt.Strings)
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var txtKey = line[..eq];
                    var txtVal = line[(eq + 1)..];
                    switch (txtKey)
                    {
                        case "serverId": serverId = txtVal; break;
                        case "version":  version  = txtVal; break;
                        case "name":     nameProp = txtVal; break;
                    }
                }
            }

            // Resolve an IP for the SRV target. Prefer IPv4 because the
            // typical home LAN doesn't expose IPv6 on link-local — clients
            // hitting an AAAA address will silently time out.
            IPAddress? ip = a4?.Address ?? a6?.Address;
            if (ip is null)
            {
                _log.LogDebug("mDNS instance {Instance}: no A/AAAA record yet, skipping.", instLbl);
                return;
            }

            ushort port = srv?.Port ?? 8080;

            // Pick a friendly name. TXT "name" wins, then the instance label
            // up to the first dot (Bonjour convention strips the service
            // suffix), then a literal fallback so the card never renders blank.
            string displayName = !string.IsNullOrWhiteSpace(nameProp)
                ? nameProp
                : InstanceLabelToFriendly(instLbl);

            // Synthesize the BaseUrl. The server always publishes over HTTP
            // on the LAN (Caddy fronts HTTPS only on the public hostname);
            // mDNS by definition is link-local so http:// is correct here.
            var host    = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{ip}]"
                : ip.ToString();
            var baseUrl = $"http://{host}:{port}";

            // Key by serverId when present so a server with two NICs doesn't
            // appear twice. Fall back to instance label as a stable-enough
            // dedup key when the TXT didn't make it through.
            var dedupKey = !string.IsNullOrWhiteSpace(serverId) ? serverId : instLbl;
            var record = new DiscoveredServer(displayName, baseUrl, serverId, version);
            _seen[dedupKey] = record;

            _log.LogDebug(
                "mDNS discovered: name={Name} baseUrl={BaseUrl} serverId={ServerId} version={Version}",
                displayName, baseUrl, serverId, version);
        }
        catch (Exception ex)
        {
            // Don't let one malformed announcement kill the listener.
            _log.LogDebug(ex, "mDNS: failed to parse instance event.");
        }
    }

    /// <summary>Bonjour instance labels look like
    /// <c>"Living-Room._animarr._tcp.local"</c>. Strip the service suffix so
    /// the UI gets the friendly chunk only.</summary>
    private static string InstanceLabelToFriendly(string instanceLabel)
    {
        var firstDot = instanceLabel.IndexOf('.');
        return firstDot > 0 ? instanceLabel[..firstDot] : instanceLabel;
    }
#endif

    public ValueTask DisposeAsync()
    {
#if ANDROID
        try { _nsd?.Dispose(); } catch { }
#else
        try { _sd.Dispose(); } catch { }
        try { _mdns.Dispose(); } catch { }
#endif
        return ValueTask.CompletedTask;
    }
}

/// <summary>DTO returned to JS via window.animarrMdnsScan(). Field names match
/// the JsServer record consumed by Discovery.razor.</summary>
public sealed record DiscoveredServer(string Name, string BaseUrl, string ServerId, string Version);
