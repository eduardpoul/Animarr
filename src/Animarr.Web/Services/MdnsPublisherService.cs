using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Makaretu.Dns;

namespace Animarr.Web.Services;

/// <summary>
/// Publishes an mDNS / Bonjour record for this Animarr install so clients on
/// the same LAN can discover it without typing IPs. Service type is
/// <c>_animarr._tcp.local.</c> — the Discovery page (Animarr.UI) browses for
/// that key when the user picks "Find on network" during first-run setup.
///
/// TXT records:
///   • <c>serverId=&lt;guid&gt;</c> — stable install GUID (matches
///     <c>ServerInfoDto.ServerId</c>, persisted in AppConfig key "server.id").
///   • <c>version=&lt;assemblyinfo&gt;</c> — informational version of the
///     running Animarr.Web build.
///   • <c>mode=multi-user</c> — capability hint for older clients.
///
/// Failure modes (all non-fatal — we log a warning and exit gracefully so the
/// rest of the app keeps booting):
///   • Docker bridge networking blocks LAN multicast (UDP 224.0.0.251:5353).
///     Workaround: <c>--network host</c> or a sidecar mDNS reflector.
///   • Two NICs on different subnets — Makaretu binds to all by default but
///     can fail if one carries no IPv4. We swallow that.
/// </summary>
public sealed class MdnsPublisherService : IHostedService, IDisposable
{
    private const string ServiceType    = "_animarr._tcp";
    private const string ServerIdKey    = "server.id";
    private const string ServerNameKey  = "server.name";

    private readonly ILogger<MdnsPublisherService> _logger;
    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly IConfiguration                _config;

    private MulticastService?  _mdns;
    private ServiceDiscovery?  _discovery;
    private ServiceProfile?    _profile;
    /// <summary>Periodic re-announce timer. Makaretu's `Announce()` is a
    /// one-shot at startup; without re-broadcasting, clients that join the
    /// network later (TV booting, phone walking back into WiFi range) won't
    /// see the server until they actively query — and some Windows / Android
    /// stacks query so rarely the registration is effectively invisible. A
    /// 15-second cadence keeps mDNS announcements live without spamming
    /// the network.</summary>
    private Timer? _reannounceTimer;

    public MdnsPublisherService(
        ILogger<MdnsPublisherService> logger,
        IServiceScopeFactory          scopeFactory,
        IConfiguration                config)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _config       = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve identity + display name from AppConfig — the server-info
            // endpoint lazy-creates the GUID on first probe; if the endpoint
            // hasn't been hit yet (very first boot) we create it here so the
            // mDNS record carries a stable value.
            string serverId;
            string serverName;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var config = scope.ServiceProvider.GetRequiredService<IAppConfigService>();

                serverId = await config.GetAsync(ServerIdKey, cancellationToken) ?? "";
                if (string.IsNullOrWhiteSpace(serverId))
                {
                    serverId = Guid.NewGuid().ToString();
                    await config.SetAsync(ServerIdKey, serverId, cancellationToken);
                }

                serverName = await config.GetAsync(ServerNameKey, cancellationToken) ?? "";
                if (string.IsNullOrWhiteSpace(serverName))
                    serverName = "Animarr";
            }

            // Port — match the listening one. ASPNETCORE_URLS = "http://+:8080"
            // is the Docker default; production behind Caddy still hits the
            // raw port on the LAN, so 8080 is the right advertisement target.
            var port = ResolveHttpPort();

            // Pick the first non-loopback IPv4 we can find. Makaretu enumerates
            // all NICs internally for the actual broadcast, but we have to seed
            // ServiceProfile with at least one address so the SRV record points
            // somewhere sensible if the client falls back to A/AAAA resolution.
            var addresses = LocalIPv4Addresses().ToList();
            if (addresses.Count == 0)
            {
                _logger.LogWarning("mDNS: no non-loopback IPv4 address — skipping publication.");
                return;
            }

            var instanceName = SanitiseInstanceName(serverName);
            _profile = new ServiceProfile(
                instanceName: instanceName,
                serviceName:  ServiceType,
                port:         (ushort)port,
                addresses:    addresses);

            // TXT records — the discovery client reads these to render the
            // server card without needing a follow-up HTTP probe. Same key/value
            // set as ServerInfoDto so the two channels agree.
            var version = typeof(MdnsPublisherService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(MdnsPublisherService).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
            _profile.AddProperty("serverId", serverId);
            _profile.AddProperty("version",  version);
            _profile.AddProperty("mode",     "multi-user");
            _profile.AddProperty("name",     serverName);

            // Filter NICs: Makaretu by default binds to EVERY interface that
            // has a UP status, which on Unraid/Docker hosts means dozens of
            // Docker veth pairs, custom bridges (br-*), docker0, tailscale,
            // etc. Multicast send/receive across that many sockets is
            // unreliable — packets often go out on a virtual veth and never
            // reach the physical LAN. We restrict Makaretu to physical /
            // host-bridge NICs only.
            _mdns = new MulticastService(filter: nics =>
            {
                var keep = new List<NetworkInterface>();
                foreach (var nic in nics)
                {
                    var name = nic.Name;
                    // Skip Docker plumbing — these interfaces don't reach the
                    // real LAN, and binding to them confuses the multicast
                    // socket layer.
                    if (name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("br-",    StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("veth",   StringComparison.OrdinalIgnoreCase)) continue;
                    // Tailscale handles its own mDNS-style discovery via the
                    // tailnet (MagicDNS). We don't want our broadcasts
                    // bleeding into the VPN.
                    if (name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("tun",       StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("wg",        StringComparison.OrdinalIgnoreCase)) continue;
                    // Loopback never reaches another host.
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    keep.Add(nic);
                }
                return keep;
            });
            _discovery = new ServiceDiscovery(_mdns);

            _mdns.NetworkInterfaceDiscovered += (_, e) =>
            {
                // Log the actual NICs Makaretu chose to bind to. Crucial for
                // diagnosing "no answers" issues on Docker / Unraid where the
                // process may end up listening on br0 / bond0 but not on the
                // physical eth that clients are on.
                foreach (var nic in e.NetworkInterfaces)
                {
                    _logger.LogInformation("mDNS NIC discovered: {Name} ({Description})",
                        nic.Name, nic.Description);
                }
                // Re-announce on NIC topology changes so a freshly-joined
                // interface (WiFi roam, ethernet plug) gets PTR records sent
                // immediately rather than waiting for the next periodic tick.
                try
                {
                    if (_profile is not null && _discovery is not null)
                        _discovery.Announce(_profile);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "mDNS re-announce on NIC change failed");
                }
            };

            // Diagnostic: log incoming queries so we can verify clients reach
            // us. On Unraid + UDR the multicast path can silently drop and
            // this is the cleanest way to confirm the receive side works.
            _mdns.QueryReceived += (_, e) =>
            {
                if (e.Message.Questions is { Count: > 0 } qs)
                {
                    foreach (var q in qs)
                    {
                        // Only log queries that touch our service type so we
                        // don't spam the log with Spotify / AppleTV / etc.
                        var name = q.Name.ToString();
                        if (name.Contains(ServiceType, StringComparison.OrdinalIgnoreCase))
                            _logger.LogInformation("mDNS query: {Name} {Type}", name, q.Type);
                    }
                }
            };

            // Order matters: start the MulticastService first (binds the
            // sockets) THEN advertise so the SD layer can announce immediately
            // on the now-open transports.
            _mdns.Start();
            _discovery.Advertise(_profile);
            _discovery.Announce(_profile);

            // Periodic re-announce. RFC 6762 §8.3 allows / encourages this for
            // shared services so late-joining clients don't have to wait for
            // a query cycle. 15 seconds is well under the typical mDNS cache
            // TTL but rare enough to be invisible to packet captures.
            _reannounceTimer = new Timer(_ =>
            {
                try
                {
                    if (_profile is not null && _discovery is not null)
                        _discovery.Announce(_profile);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "mDNS periodic re-announce failed");
                }
            }, state: null, dueTime: TimeSpan.FromSeconds(15), period: TimeSpan.FromSeconds(15));

            _logger.LogInformation(
                "mDNS published: instance='{Instance}' type='{Service}' port={Port} serverId={ServerId}",
                instanceName, ServiceType, port, serverId);
        }
        catch (Exception ex)
        {
            // Soft failure — Docker bridge networking, restricted NICs, etc.
            // The rest of the app continues; manual server URL entry still
            // works in Discovery.
            _logger.LogWarning(ex,
                "mDNS publication failed (LAN discovery disabled). " +
                "If running in Docker use --network host or a sidecar reflector.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _reannounceTimer?.Dispose(); } catch { }
        try
        {
            if (_discovery is not null && _profile is not null)
                _discovery.Unadvertise(_profile);
        }
        catch { /* shutting down — ignore */ }
        try { _mdns?.Stop(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _reannounceTimer?.Dispose(); } catch { }
        try { _discovery?.Dispose(); } catch { }
        try { _mdns?.Dispose(); } catch { }
    }

    /// <summary>Best-effort: read the first port from ASPNETCORE_URLS or fall
    /// back to 8080 (Animarr's Docker default).</summary>
    private int ResolveHttpPort()
    {
        var urls = _config["ASPNETCORE_URLS"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? "";
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(raw.Replace("+", "0.0.0.0"), UriKind.Absolute, out var u))
                if (u.Port > 0) return u.Port;
        }
        return 8080;
    }

    /// <summary>Enumerate local IPv4 addresses excluding loopback + link-local
    /// + Docker / VPN plumbing. Same filter set as the MulticastService NIC
    /// filter above — if those NICs don't get multicast sockets, they
    /// shouldn't end up in the SRV/A record set either.</summary>
    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var name = nic.Name;
            if (name.StartsWith("docker",    StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("br-",       StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("veth",      StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("tun",       StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("wg",        StringComparison.OrdinalIgnoreCase)) continue;

            var ipProps = nic.GetIPProperties();
            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                // 169.254.x.x — APIPA. Skip; broadcasting on those just spams.
                var bytes = addr.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                // 172.17.x.x — default Docker bridge subnet. Even if the
                // bridge interface itself is filtered, the host may have a
                // virtual IP in this range that we don't want to broadcast.
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) continue;
                yield return addr.Address;
            }
        }
    }

    /// <summary>Bonjour instance names can't contain "." (it's the label
    /// separator) — replace with a hyphen so a server called "Living Room.TV"
    /// publishes cleanly.</summary>
    private static string SanitiseInstanceName(string raw)
    {
        var name = raw.Replace('.', '-').Trim();
        return string.IsNullOrEmpty(name) ? "Animarr" : name;
    }
}
