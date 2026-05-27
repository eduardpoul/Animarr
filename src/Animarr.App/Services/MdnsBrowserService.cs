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
/// Same Makaretu library as the server publishes with, so the wire format and
/// TXT-record keys match exactly (serverId / version / name / mode).
///
/// Cross-platform: Makaretu.Dns.Multicast targets netstandard2.0 and binds
/// sockets via <c>System.Net.NetworkInformation.NetworkInterface</c> which is
/// available on every MAUI head (Windows / macOS / Android / iOS / Catalyst).
/// Pure browser WASM can't open a UDP 5353 socket and never lands here —
/// the Discovery page falls back to manual URL entry in that case.
///
/// Lifecycle: registered as a singleton in MauiProgram.cs and the
/// MulticastService instance lives for the whole app process. We also stash
/// the instance in a static field (<see cref="Instance"/>) so the
/// [JSInvokable] static dispatch in <c>MdnsBridge</c> can find us without
/// having to round-trip a DotNetObjectReference through every JS call.
/// </summary>
public sealed class MdnsBrowserService : IAsyncDisposable
{
    private const string ServiceType = "_animarr._tcp";

    private readonly ILogger<MdnsBrowserService> _log;
    private readonly MulticastService              _mdns;
    private readonly ServiceDiscovery              _sd;
    private readonly ConcurrentDictionary<string, DiscoveredServer> _seen = new();

    /// <summary>Process-wide accessor for the singleton instance — set by
    /// MauiProgram once the service container is built so the static
    /// [JSInvokable] in MdnsBridge can reach the browser without juggling a
    /// DotNetObjectReference per page nav.</summary>
    public static MdnsBrowserService? Instance { get; private set; }

    public IReadOnlyCollection<DiscoveredServer> Servers => _seen.Values.ToArray();

    public MdnsBrowserService(ILogger<MdnsBrowserService> log)
    {
        _log = log;

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
    }

    /// <summary>Static initializer — call from MauiProgram after the
    /// container is built. Idempotent.</summary>
    public static void RegisterStaticInstance(MdnsBrowserService svc) => Instance = svc;

    /// <summary>Re-issue a fresh query and wait briefly so newly woken-up
    /// servers have a chance to answer. Returns the dedup'd snapshot of
    /// everything we've seen — including stale entries from earlier in the
    /// session, since the publisher's reannounce TTL might leave them
    /// unrefreshed even when they're still reachable.</summary>
    public async Task<DiscoveredServer[]> BrowseAsync(int timeoutMs = 4000)
    {
        try
        {
            _sd.QueryServiceInstances(ServiceType);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS re-query failed.");
        }

        // Wait for late responders. The event handler stuffs new servers into
        // _seen on the multicast listener thread, so by the time the delay
        // ends the snapshot we return reflects them.
        await Task.Delay(timeoutMs).ConfigureAwait(false);
        return _seen.Values.ToArray();
    }

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

    public ValueTask DisposeAsync()
    {
        try { _sd.Dispose(); } catch { }
        try { _mdns.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>DTO returned to JS via window.animarrMdnsScan(). Field names match
/// the JsServer record consumed by Discovery.razor.</summary>
public sealed record DiscoveredServer(string Name, string BaseUrl, string ServerId, string Version);
