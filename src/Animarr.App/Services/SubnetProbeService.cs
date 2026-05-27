using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Animarr.App.Services;

/// <summary>
/// TCP-based fallback discovery for networks where mDNS multicast can't cross
/// the segment between the server and the client (common with consumer WiFi
/// APs that filter multicast between wired and wireless, or between SSIDs).
///
/// Strategy:
///   1. Enumerate this device's IPv4 NICs, derive each one's /24 subnet.
///   2. Fan out TCP connect attempts to a fixed set of likely Animarr ports
///      (8080 = host networking default, 8450 = docker-compose default).
///   3. For every reachable host:port, HTTP GET /api/server/info — if it
///      shape-matches a ServerInfoDto, surface it as a discovered server.
///
/// Slow compared to mDNS (still under ~5s for a /24 with the right
/// parallelism + connect timeout), but immune to multicast filtering. The
/// Discovery page calls this only when mDNS returned nothing.
/// </summary>
public sealed class SubnetProbeService
{
    private static readonly int[] CandidatePorts = [8080, 8450];

    private readonly HttpClient _http;
    private readonly ILogger<SubnetProbeService> _log;

    public SubnetProbeService(ILogger<SubnetProbeService> log)
    {
        _log = log;
        // Dedicated HttpClient — the request-rewriting one in MauiProgram
        // forces every call to the active server. For probing we need to hit
        // arbitrary hosts as-typed.
        // 2s HTTP timeout: mobile WiFi can stall a TCP handshake well past
        // 250ms even to a perfectly healthy host on the same subnet (radio
        // duty cycling, sleep, etc.). Going too low risks missing the real
        // server — and a few seconds extra on a fallback scan is cheap.
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(2000) };
    }

    /// <summary>Probe every /24 the current device is attached to. Each host
    /// gets a 600ms TCP-connect window then a 2s HTTP GET — a typical /24
    /// finishes in ~5s with the parallelism set below. Returns all servers
    /// whose /api/server/info answers with the expected shape.</summary>
    public async Task<DiscoveredServer[]> ProbeSubnetsAsync(int connectTimeoutMs = 600, CancellationToken ct = default)
    {
        DiagLog("SubnetProbe: starting subnet scan.");
        var subnets = LocalSubnets().ToList();
        if (subnets.Count == 0)
        {
            DiagLog("SubnetProbe: no usable /24 subnets — skipping.");
            return Array.Empty<DiscoveredServer>();
        }

        var targets = new List<(IPAddress ip, int port)>();
        foreach (var subnet in subnets)
        {
            // /24 host range — 1..254, skip .0 (network) and .255 (broadcast).
            for (int host = 1; host <= 254; host++)
            {
                var bytes = subnet.GetAddressBytes();
                bytes[3] = (byte)host;
                var ip = new IPAddress(bytes);
                foreach (var port in CandidatePorts) targets.Add((ip, port));
            }
        }

        DiagLog($"SubnetProbe: probing {targets.Count} host:port combos (subnets: {string.Join(", ", subnets)}).");

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, DiscoveredServer>();
        // 48 concurrent — Android's socket pool is happy up to ~256 but going
        // wide on mobile WiFi tends to drop later batches when the radio is
        // saturated. 48 keeps the scan fast (~5s for /24) without missing
        // hosts further down the list.
        using var sem = new SemaphoreSlim(initialCount: 48);

        var tasks = targets.Select(async t =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var server = await TryProbeAsync(t.ip, t.port, connectTimeoutMs, ct).ConfigureAwait(false);
                if (server is not null)
                {
                    var key = !string.IsNullOrWhiteSpace(server.ServerId) ? server.ServerId : server.BaseUrl;
                    found[key] = server;
                }
            }
            catch { /* per-host failure is expected and uninteresting */ }
            finally { sem.Release(); }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        DiagLog($"SubnetProbe: scan done — found {found.Count} server(s).");
        return found.Values.ToArray();
    }

    private async Task<DiscoveredServer?> TryProbeAsync(IPAddress ip, int port, int connectTimeoutMs, CancellationToken ct)
    {
        // Hosts on the same /24 as us are worth a closer look in logs — if a
        // user reports "scan empty" we want to see exactly why .200 specifically
        // dropped out. Cellular noise (10.x.x.x) we don't bother tracing.
        var isLikelyTarget = ip.GetAddressBytes()[0] == 192;

        // Stage 1: TCP connect with short timeout. Skip hosts that don't even
        // have the port open — saves us a full HTTP timeout per dead address.
        using (var tcp = new TcpClient { NoDelay = true })
        {
            try
            {
                var connectTask = tcp.ConnectAsync(ip, port);
                var timeout = Task.Delay(connectTimeoutMs, ct);
                var winner = await Task.WhenAny(connectTask, timeout).ConfigureAwait(false);
                if (winner != connectTask)
                {
                    if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {ip}:{port} TCP connect timed out after {connectTimeoutMs}ms.");
                    return null;
                }
                await connectTask.ConfigureAwait(false);
                if (!tcp.Connected)
                {
                    if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {ip}:{port} TCP not connected after await.");
                    return null;
                }
                if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {ip}:{port} TCP connected.");
            }
            catch (Exception ex)
            {
                if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {ip}:{port} TCP threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Stage 2: GET /api/server/info on the live port. Shape-match against
        // our DTO to avoid false positives from random web services.
        var url = $"http://{ip}:{port}/api/server/info";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {url} HTTP {(int)resp.StatusCode}.");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {url} empty body.");
                return null;
            }

            // Tiny manual JSON peek — we don't pull in System.Text.Json types
            // here because the response is tiny and predictable. Just check
            // the "serverId" marker exists and pull friendly fields.
            if (!json.Contains("\"serverId\"", StringComparison.Ordinal))
            {
                if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {url} no serverId in body ({json.Length} chars).");
                return null;
            }

            var name     = ExtractString(json, "name")     ?? "Animarr";
            var version  = ExtractString(json, "version")  ?? "0.0.0";
            var serverId = ExtractString(json, "serverId") ?? "";

            var baseUrl = $"http://{ip}:{port}";
            DiagLog($"SubnetProbe: hit {baseUrl} name='{name}' serverId={serverId}");
            return new DiscoveredServer(name, baseUrl, serverId, version);
        }
        catch (Exception ex)
        {
            if (isLikelyTarget && port == 8080) DiagLog($"SubnetProbe: {url} HTTP threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Cheapest possible JSON string extraction — finds <c>"key":"value"</c>
    /// patterns. The /api/server/info shape never includes special characters
    /// in these fields so we don't need a real parser.</summary>
    private static string? ExtractString(string json, string key)
    {
        var needle = $"\"{key}\":\"";
        var i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i += needle.Length;
        var end = json.IndexOf('"', i);
        if (end < 0) return null;
        return json[i..end];
    }

    /// <summary>All IPv4 /24s the device is attached to. Filter out loopback,
    /// link-local (169.254.x.x), and any non-IPv4 to keep the scan size sane.
    /// Returns the network address (last octet zeroed) for each /24.</summary>
    private static IEnumerable<IPAddress> LocalSubnets()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        foreach (var nic in nics)
        {
            var unicast = nic.GetIPProperties().UnicastAddresses;
            foreach (var addr in unicast)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = addr.Address.GetAddressBytes();
                // Skip link-local — Animarr never lives there.
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                // Treat IPv4 as /24 for the scan. Reasonable default for home
                // LANs; users on /16 corp networks aren't the target audience
                // and they can always type the URL manually.
                bytes[3] = 0;
                yield return new IPAddress(bytes);
            }
        }
    }

    private static void DiagLog(string msg)
    {
#if ANDROID
        try { Android.Util.Log.Info("Animarr.mDNS", msg); } catch { }
#endif
    }
}
