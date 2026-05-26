using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Animarr.Web.Services;

/// <summary>
/// DLNA "Send-to-TV" — i.e. UPnP ControlPoint side.
///
/// Pairs with <see cref="DlnaService"/>: while DlnaService advertises Animarr
/// AS a MediaServer, this service discovers MediaRenderers (TVs, AVRs,
/// speakers) on the LAN and POSTs SOAP actions to their AVTransport service
/// to start playback of Animarr URLs.
///
/// Flow when the user hits "Cast" in the UI:
///   1. UI fetches /api/dlna/renderers      → list of discovered TVs
///   2. User picks one
///   3. UI POSTs /api/dlna/play             → server resolves AVTransport
///      control URL from cached renderer info and fires SetAVTransportURI
///      + Play SOAP calls. TV starts pulling Animarr's /api/video stream.
///
/// Discovery strategy: opportunistic. We keep a small in-memory cache keyed
/// by UDN; entries are populated by two channels:
///   • Active scan — RefreshAsync() sends an M-SEARCH and waits ~3s for
///     unicast replies on an ephemeral port. Triggered when the UI asks
///     for the renderer list, with a short throttle so it doesn't flood.
///   • Passive — NOTIFY ssdp:alive frames received on the main DlnaService
///     SSDP socket are forwarded here so renderers that announce themselves
///     between scans show up immediately.
///
/// Description fetch (LOCATION URL → device.xml) is done lazily, and the
/// extracted control URL is cached so subsequent Plays skip the HTTP roundtrip.
/// </summary>
public sealed class DlnaCastService : IDisposable
{
    public sealed record RendererInfo(
        string Udn,
        string FriendlyName,
        string? Manufacturer,
        string? ModelName,
        Uri    Location,
        Uri    AvTransportControlUrl)
    {
        /// <summary>Wall-clock time of the latest SSDP frame that confirmed
        /// this renderer is online. RefreshAsync prunes entries older than
        /// the cache TTL so a powered-off TV stops showing in the menu.</summary>
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Discard renderer entries that haven't re-announced in this
    /// long. Renderers re-NOTIFY their `max-age` worth which is typically
    /// 30-60 minutes, so 90 minutes is a generous-but-not-infinite window.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(90);

    private const string SsdpAddr = "239.255.255.250";
    private const int    SsdpPort = 1900;
    private const string AvTransportType = "urn:schemas-upnp-org:service:AVTransport:1";

    private readonly ILogger<DlnaCastService> _logger;
    private readonly ConcurrentDictionary<string, RendererInfo> _renderers = new();
    private readonly HttpClient _http;
    private DateTime _lastScan = DateTime.MinValue;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public DlnaCastService(ILogger<DlnaCastService> logger, IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _http   = httpFactory.CreateClient("dlna-cast");
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public IReadOnlyCollection<RendererInfo> KnownRenderers
    {
        get
        {
            EvictStale();
            return _renderers.Values.ToArray();
        }
    }

    private void EvictStale()
    {
        var cutoff = DateTime.UtcNow - CacheTtl;
        foreach (var (k, v) in _renderers)
            if (v.LastSeen < cutoff) _renderers.TryRemove(k, out _);
    }

    // ─── Active discovery (M-SEARCH burst) ──────────────────────────────────

    public async Task<IReadOnlyCollection<RendererInfo>> RefreshAsync(CancellationToken ct = default)
    {
        // Throttle: at most one full M-SEARCH every 3 seconds. The cache
        // already has whatever passive NOTIFY frames brought in.
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 3 && _renderers.Count > 0)
            return _renderers.Values.ToArray();

        await _scanLock.WaitAsync(ct);
        try
        {
            _lastScan = DateTime.UtcNow;
            await ProbeOnceAsync(AvTransportType, ct);
            // Plus root-device, since some renderers don't separately respond
            // to service-type searches.
            await ProbeOnceAsync("urn:schemas-upnp-org:device:MediaRenderer:1", ct);
            EvictStale();
            return _renderers.Values.ToArray();
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ProbeOnceAsync(string st, CancellationToken ct)
    {
        // Ephemeral unicast socket. We deliberately do NOT bind to port 1900
        // — that's the multicast listener owned by DlnaService (and Emby).
        // Renderers reply unicast to the source port we used to send.
        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            ExclusiveAddressUse = false,
        };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.Client.SendTimeout    = 1000;
        udp.Client.ReceiveTimeout = 200;

        var packet = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {SsdpAddr}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {st}\r\n" +
            "\r\n");

        try
        {
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Parse(SsdpAddr), SsdpPort));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "M-SEARCH send failed for {St}", st);
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var recvTask = udp.ReceiveAsync();
                var winner = await Task.WhenAny(recvTask, Task.Delay(300, ct));
                if (winner != recvTask) continue;
                var res = await recvTask;
                _ = HandleSearchResponseAsync(Encoding.ASCII.GetString(res.Buffer), ct);
            }
            catch { /* timeout / cancellation — let the deadline govern */ }
        }
    }

    /// <summary>
    /// Forwarded by DlnaService when it sees a NOTIFY ssdp:alive on the
    /// shared multicast socket. Lets renderers that announce themselves
    /// between active scans show up in the cache without waiting.
    /// </summary>
    public Task IngestNotifyAsync(string packet, CancellationToken ct = default)
        => HandleSearchResponseAsync(packet, ct);

    // ─── Description fetch ──────────────────────────────────────────────────

    private async Task HandleSearchResponseAsync(string text, CancellationToken ct)
    {
        // Parse out LOCATION and ST/NT header. We only care if it's something
        // that *could* be a MediaRenderer.
        string? location = null;
        string? typeHdr  = null;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                location = l[9..].Trim();
            else if (l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
                typeHdr = l[3..].Trim();
            else if (l.StartsWith("NT:", StringComparison.OrdinalIgnoreCase))
                typeHdr = l[3..].Trim();
        }
        if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var locUri)) return;

        // Type hint speeds up filtering; otherwise we'll still try to parse
        // the description and reject if it's not a MediaRenderer.
        if (typeHdr is not null &&
            !typeHdr.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase) &&
            !typeHdr.Contains("AVTransport",   StringComparison.OrdinalIgnoreCase) &&
            !typeHdr.Contains("rootdevice",    StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var desc = await _http.GetStringAsync(locUri, ct);
            var info = ParseRenderer(desc, locUri);
            if (info is null) return;
            // If we already had this renderer, just bump LastSeen so it
            // survives the TTL eviction. New entries get the default LastSeen
            // from the record initializer.
            if (_renderers.TryGetValue(info.Udn, out var existing))
            {
                existing.LastSeen = DateTime.UtcNow;
            }
            else
            {
                _renderers[info.Udn] = info;
                _logger.LogInformation("DLNA-Cast: discovered renderer {Name} ({Mfg}) at {Url}",
                    info.FriendlyName, info.Manufacturer, info.AvTransportControlUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DLNA-Cast: failed to fetch description {Url}", locUri);
        }
    }

    private static RendererInfo? ParseRenderer(string xml, Uri location)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("d", "urn:schemas-upnp-org:device-1-0");

            string Pick(string xpath) =>
                doc.SelectSingleNode(xpath, ns)?.InnerText.Trim() ??
                doc.SelectSingleNode(xpath.Replace("d:", ""), ns)?.InnerText.Trim() ??
                "";

            var deviceType = Pick("//d:device/d:deviceType");
            // Some devices nest MediaRenderer as embeddedDeviceList; we accept
            // them too as long as the root has AVTransport somewhere.
            var hasAvTransport = doc.SelectNodes("//d:service[d:serviceType='" + AvTransportType + "']", ns);
            if ((hasAvTransport is null || hasAvTransport.Count == 0)
                && !deviceType.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase))
                return null;

            var udn          = Pick("//d:device/d:UDN");
            var friendlyName = Pick("//d:device/d:friendlyName");
            var manufacturer = Pick("//d:device/d:manufacturer");
            var modelName    = Pick("//d:device/d:modelName");

            // Find AVTransport service node — anywhere in the description tree.
            XmlNode? avSvc = doc.SelectSingleNode("//d:service[d:serviceType='" + AvTransportType + "']", ns);
            if (avSvc is null) return null;

            var controlUrlText = avSvc["controlURL", "urn:schemas-upnp-org:device-1-0"]?.InnerText?.Trim()
                              ?? avSvc.SelectSingleNode("d:controlURL", ns)?.InnerText?.Trim();
            if (string.IsNullOrEmpty(controlUrlText)) return null;
            // Per UPnP spec: <URLBase> is dead in 1.1, controlURL is resolved
            // relative to the description location URI.
            var controlUri = new Uri(location, controlUrlText);

            if (string.IsNullOrEmpty(udn) || string.IsNullOrEmpty(friendlyName)) return null;
            return new RendererInfo(udn, friendlyName, manufacturer, modelName, location, controlUri);
        }
        catch
        {
            return null;
        }
    }

    // ─── SOAP — SetAVTransportURI + Play ────────────────────────────────────

    public async Task<bool> PlayAsync(string udn, string streamUrl, string title, string mime,
        CancellationToken ct = default)
    {
        if (!_renderers.TryGetValue(udn, out var renderer))
        {
            _logger.LogWarning("DLNA-Cast: renderer {Udn} not in cache — refresh first", udn);
            return false;
        }

        var didl = BuildDidl(title, streamUrl, mime);
        var setOk = await SendSoapAsync(renderer.AvTransportControlUrl, "SetAVTransportURI", $"""
            <InstanceID>0</InstanceID>
            <CurrentURI>{Xml(streamUrl)}</CurrentURI>
            <CurrentURIMetaData>{Xml(didl)}</CurrentURIMetaData>
            """, ct);
        if (!setOk) return false;

        // Some renderers need a beat between SetAVTransportURI and Play.
        await Task.Delay(250, ct);

        return await SendSoapAsync(renderer.AvTransportControlUrl, "Play", """
            <InstanceID>0</InstanceID>
            <Speed>1</Speed>
            """, ct);
    }

    private async Task<bool> SendSoapAsync(Uri controlUrl, string action, string innerBody, CancellationToken ct)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{AvTransportType}">
                  {innerBody}
                </u:{action}>
              </s:Body>
            </s:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        req.Headers.Add("SOAPAction", $"\"{AvTransportType}#{action}\"");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DLNA-Cast: {Action} → {Status} on {Url}: {Body}", action, resp.StatusCode, controlUrl, err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLNA-Cast: {Action} failed on {Url}", action, controlUrl);
            return false;
        }
    }

    // ─── DIDL-Lite for the item we're casting ──────────────────────────────

    private static string BuildDidl(string title, string url, string mime)
    {
        // upnp:class must be present or many TVs reject the URI. videoItem
        // covers movies + episodes alike for cast purposes.
        var proto = $"http-get:*:{mime}:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        return $"""
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" xmlns:dlna="urn:schemas-dlna-org:metadata-1-0/">
              <item id="0" parentID="-1" restricted="1">
                <dc:title>{Xml(title)}</dc:title>
                <upnp:class>object.item.videoItem</upnp:class>
                <res protocolInfo="{Xml(proto)}">{Xml(url)}</res>
              </item>
            </DIDL-Lite>
            """;
    }

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    public void Dispose() { _scanLock.Dispose(); }
}
