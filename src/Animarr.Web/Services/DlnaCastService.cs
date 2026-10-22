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
///
/// The passive channel sees every SSDP NOTIFY on the LAN (routers, NAS,
/// printers, speakers re-announce every few minutes, several NTs per burst),
/// whether or not anyone ever casts. So: only MediaRenderer/AVTransport
/// announcements are considered (a renderer always announces its device type
/// too, so the old catch-all <c>rootdevice</c> arm only added non-renderers),
/// each LOCATION's outcome is cached (renderer → just bump LastSeen for an
/// hour; not-a-renderer / unreachable → skip for a while on the passive path),
/// duplicate in-flight fetches of one LOCATION are dropped, and at most
/// <see cref="MaxConcurrentFetches"/> description fetches run at once.
/// </summary>
public sealed class DlnaCastService : IDisposable
{
    public sealed record RendererInfo(
        string Udn,
        string FriendlyName,
        string? Manufacturer,
        string? ModelName,
        Uri    Location,
        Uri    AvTransportControlUrl,
        Uri?   ConnectionManagerControlUrl = null)
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
    private const string ConnectionManagerType = "urn:schemas-upnp-org:service:ConnectionManager:1";

    /// <summary>A renderer's Sink protocol list is asked once per this long.</summary>
    private static readonly TimeSpan SinkTtl = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string>? Sink, DateTime Until)> _sinks = new();

    /// <summary>A known renderer's LOCATION isn't re-fetched for this long —
    /// its re-announces just bump LastSeen.</summary>
    private static readonly TimeSpan RendererRecheck   = TimeSpan.FromHours(1);
    /// <summary>A description that isn't a renderer (no AVTransport) is skipped
    /// this long on the passive path.</summary>
    private static readonly TimeSpan NotRendererTtl    = TimeSpan.FromHours(24);
    /// <summary>An unreachable/broken LOCATION (other subnet, device still
    /// booting) is skipped this long on the passive path; an active scan
    /// (user opened the Cast menu) always retries it.</summary>
    private static readonly TimeSpan FetchFailedTtl    = TimeSpan.FromMinutes(10);
    private const int MaxConcurrentFetches = 4;
    private const int MaxLocations         = 256;

    /// <summary>What a LOCATION's description told us: the renderer UDN, or null
    /// for not-a-renderer / failed, valid until <c>Until</c>.</summary>
    private sealed record LocationEntry(string? Udn, DateTime Until);

    private readonly ILogger<DlnaCastService> _logger;
    private readonly ConcurrentDictionary<string, RendererInfo> _renderers = new();
    private readonly ConcurrentDictionary<string, LocationEntry> _locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fetchGate = new(MaxConcurrentFetches, MaxConcurrentFetches);
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

        // One outstanding receive at a time, cancelled by the 3 s window. (The
        // old loop started a fresh ReceiveAsync every 300 ms without cancelling
        // the previous one, so a datagram could land in an abandoned receive and
        // that renderer's reply was lost.)
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(3));
        while (!window.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(window.Token);
                _ = HandleSearchResponseAsync(Encoding.ASCII.GetString(res.Buffer), activeScan: true, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* e.g. ICMP port-unreachable reset on Windows — keep listening */ }
            catch (ObjectDisposedException) { break; }
        }
    }

    /// <summary>
    /// Forwarded by DlnaService when it sees a NOTIFY ssdp:alive on the
    /// shared multicast socket. Lets renderers that announce themselves
    /// between active scans show up in the cache without waiting.
    /// </summary>
    public Task IngestNotifyAsync(string packet, CancellationToken ct = default)
        => HandleSearchResponseAsync(packet, activeScan: false, ct);

    // ─── Description fetch ──────────────────────────────────────────────────

    private async Task HandleSearchResponseAsync(string text, bool activeScan, CancellationToken ct)
    {
        // Parse out LOCATION and ST/NT header. We only care if it's something
        // that *could* be a MediaRenderer.
        string? location = null;
        string? typeHdr  = null;
        string? nts      = null;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                location = l[9..].Trim();
            else if (l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
                typeHdr = l[3..].Trim();
            else if (l.StartsWith("NT:", StringComparison.OrdinalIgnoreCase))
                typeHdr = l[3..].Trim();
            else if (l.StartsWith("NTS:", StringComparison.OrdinalIgnoreCase))
                nts = l[4..].Trim();
        }
        // A device going away has nothing to fetch.
        if (nts is not null && nts.Contains("byebye", StringComparison.OrdinalIgnoreCase)) return;
        if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var locUri)) return;

        // Renderers announce their device type (MediaRenderer) and services
        // (AVTransport) alongside rootdevice, so those two NTs are enough — the
        // rootdevice arm only ever added routers/NAS/printers to fetch.
        if (typeHdr is not null &&
            !typeHdr.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase) &&
            !typeHdr.Contains("AVTransport",   StringComparison.OrdinalIgnoreCase))
            return;

        var now = DateTime.UtcNow;
        if (_locations.TryGetValue(location, out var known) && known.Until > now)
        {
            // Known renderer: the announce only proves it's still online.
            if (known.Udn is not null && _renderers.TryGetValue(known.Udn, out var seen))
            {
                seen.LastSeen = now;
                return;
            }
            // Not a renderer / recently unreachable — skip on the passive path;
            // an explicit scan retries (a TV that just booted may have answered
            // NOTIFY before its HTTP server was up).
            if (known.Udn is null && !activeScan) return;
        }

        // One fetch per LOCATION at a time — an announce burst is 2-6 frames.
        if (!_inflight.TryAdd(location, 0)) return;
        try
        {
            await _fetchGate.WaitAsync(ct);
            try
            {
                var desc = await _http.GetStringAsync(locUri, ct);
                var info = ParseRenderer(desc, locUri);
                if (info is null)
                {
                    RememberLocation(location, new LocationEntry(null, DateTime.UtcNow + NotRendererTtl));
                    return;
                }
                RememberLocation(location, new LocationEntry(info.Udn, DateTime.UtcNow + RendererRecheck));
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
            finally { _fetchGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            RememberLocation(location, new LocationEntry(null, DateTime.UtcNow + FetchFailedTtl));
            _logger.LogDebug(ex, "DLNA-Cast: failed to fetch description {Url}", locUri);
        }
        finally
        {
            _inflight.TryRemove(location, out _);
        }
    }

    /// <summary>Record a LOCATION outcome; the map is bounded (expired entries
    /// pruned first, then reset) so a chatty or hostile LAN can't grow it.</summary>
    private void RememberLocation(string location, LocationEntry entry)
    {
        if (_locations.Count >= MaxLocations && !_locations.ContainsKey(location))
        {
            var now = DateTime.UtcNow;
            foreach (var (k, v) in _locations)
                if (v.Until <= now) _locations.TryRemove(k, out _);
            if (_locations.Count >= MaxLocations) _locations.Clear();
        }
        _locations[location] = entry;
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

            // ConnectionManager (optional): its GetProtocolInfo Sink list says
            // which containers the renderer takes — see GetSinkProtocolsAsync.
            Uri? cmUri = null;
            var cmSvc = doc.SelectSingleNode("//d:service[d:serviceType='" + ConnectionManagerType + "']", ns);
            var cmText = cmSvc?["controlURL", "urn:schemas-upnp-org:device-1-0"]?.InnerText?.Trim()
                         ?? cmSvc?.SelectSingleNode("d:controlURL", ns)?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(cmText)) cmUri = new Uri(location, cmText);

            if (string.IsNullOrEmpty(udn) || string.IsNullOrEmpty(friendlyName)) return null;
            return new RendererInfo(udn, friendlyName, manufacturer, modelName, location, controlUri, cmUri);
        }
        catch
        {
            return null;
        }
    }

    // ─── SOAP — SetAVTransportURI + Play ────────────────────────────────────

    public async Task<bool> PlayAsync(string udn, string streamUrl, string title, string mime,
        CancellationToken ct = default)
        => await PlayAsync(udn, streamUrl, title, mime, subtitleUrl: null, protocolFlags: null, startTimeMs: null, ct);

    /// <summary>
    /// SetAVTransportURI + Play, then (in the background) a Seek to
    /// <paramref name="startTimeMs"/> once the renderer reports PLAYING — most
    /// renderers refuse a Seek before that. <paramref name="subtitleUrl"/> (an
    /// SRT) is announced in the item metadata the common ways renderers look
    /// for it: Samsung's <c>sec:CaptionInfoEx</c> / <c>sec:CaptionInfo</c>, a
    /// <c>text/srt</c> <c>res</c>, and PacketVideo's <c>pv:subtitleFileUri</c>.
    /// <paramref name="protocolFlags"/> overrides the 4th protocolInfo field
    /// (a transcoded stream: time-seek, no byte-seek).
    /// </summary>
    public async Task<bool> PlayAsync(string udn, string streamUrl, string title, string mime,
        string? subtitleUrl, string? protocolFlags, long? startTimeMs, CancellationToken ct)
    {
        if (!_renderers.TryGetValue(udn, out var renderer))
        {
            _logger.LogWarning("DLNA-Cast: renderer {Udn} not in cache — refresh first", udn);
            return false;
        }

        var didl = BuildDidl(title, streamUrl, mime, subtitleUrl, protocolFlags);
        var setOk = await SendSoapAsync(renderer.AvTransportControlUrl, "SetAVTransportURI", $"""
            <InstanceID>0</InstanceID>
            <CurrentURI>{Xml(streamUrl)}</CurrentURI>
            <CurrentURIMetaData>{Xml(didl)}</CurrentURIMetaData>
            """, ct);
        if (!setOk) return false;

        // Some renderers need a beat between SetAVTransportURI and Play.
        await Task.Delay(250, ct);

        var played = await SendSoapAsync(renderer.AvTransportControlUrl, "Play", """
            <InstanceID>0</InstanceID>
            <Speed>1</Speed>
            """, ct);
        if (played && startTimeMs is > 1000)
            _ = Task.Run(() => SeekWhenPlayingAsync(renderer, startTimeMs.Value));
        return played;
    }

    /// <summary>Wait (≤ 15 s) for the renderer to report PLAYING, then seek
    /// to the start position. Best effort — a renderer without Seek just
    /// plays from the beginning.</summary>
    private async Task SeekWhenPlayingAsync(RendererInfo renderer, long startTimeMs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var info = await SendSoapForBodyAsync(renderer.AvTransportControlUrl, AvTransportType, "GetTransportInfo",
                    "<InstanceID>0</InstanceID>", cts.Token);
                if (info is not null && info.Contains("PLAYING", StringComparison.OrdinalIgnoreCase)) break;
                await Task.Delay(500, cts.Token);
            }
            var t = TimeSpan.FromMilliseconds(startTimeMs);
            var target = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            var ok = await SendSoapAsync(renderer.AvTransportControlUrl, "Seek", $"""
                <InstanceID>0</InstanceID>
                <Unit>REL_TIME</Unit>
                <Target>{target}</Target>
                """, cts.Token);
            _logger.LogInformation("DLNA-Cast: seek {Renderer} to {Target}: {Result}", renderer.FriendlyName, target, ok ? "ok" : "refused");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DLNA-Cast: start-position seek failed on {Renderer}", renderer.FriendlyName);
        }
    }

    /// <summary>
    /// The renderer's ConnectionManager Sink protocol list
    /// (<c>http-get:*:video/x-matroska:*</c>, …) — which containers it takes.
    /// Cached per renderer; null when it has no ConnectionManager or didn't
    /// answer (then nothing is known and the caller keeps the raw file).
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetSinkProtocolsAsync(string udn, CancellationToken ct)
    {
        if (_sinks.TryGetValue(udn, out var known) && known.Until > DateTime.UtcNow) return known.Sink;
        if (!_renderers.TryGetValue(udn, out var renderer) || renderer.ConnectionManagerControlUrl is null) return null;

        IReadOnlyList<string>? sink = null;
        var body = await SendSoapForBodyAsync(renderer.ConnectionManagerControlUrl, ConnectionManagerType,
            "GetProtocolInfo", "", ct);
        if (body is not null)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(body);
                var text = doc.GetElementsByTagName("Sink").Cast<XmlNode>().FirstOrDefault()?.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sink = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (XmlException) { /* unparseable — unknown */ }
        }
        _sinks[udn] = (sink, DateTime.UtcNow + SinkTtl);
        return sink;
    }

    /// <summary>True when a Sink list accepts <paramref name="mime"/> over
    /// HTTP (exact, or a <c>*</c> wildcard entry).</summary>
    public static bool SinkAccepts(IReadOnlyList<string> sink, string mime)
    {
        foreach (var entry in sink)
        {
            var parts = entry.Split(':');
            if (parts.Length < 3 || !parts[0].Equals("http-get", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[2] == "*" || parts[2].Equals(mime, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async Task<string?> SendSoapForBodyAsync(Uri controlUrl, string serviceType, string action, string innerBody,
        CancellationToken ct)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{serviceType}">{innerBody}</u:{action}>
              </s:Body>
            </s:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        req.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "DLNA-Cast: {Action} failed on {Url}", action, controlUrl);
            return null;
        }
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

    private static string BuildDidl(string title, string url, string mime, string? subtitleUrl = null,
        string? protocolFlags = null)
    {
        // upnp:class must be present or many TVs reject the URI. videoItem
        // covers movies + episodes alike for cast purposes.
        var flags = protocolFlags ?? "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        var proto = $"http-get:*:{mime}:{flags}";
        return $"""
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" xmlns:dlna="urn:schemas-dlna-org:metadata-1-0/" xmlns:sec="http://www.sec.co.kr/" xmlns:pv="http://www.pv.com/pvns/">
              <item id="0" parentID="-1" restricted="1">
                <dc:title>{Xml(title)}</dc:title>
                <upnp:class>object.item.videoItem</upnp:class>
                <res protocolInfo="{Xml(proto)}"{SubtitleResAttributes(subtitleUrl)}>{Xml(url)}</res>{SubtitleElements(subtitleUrl)}
              </item>
            </DIDL-Lite>
            """;
    }

    /// <summary>PacketVideo-style attributes on the video <c>res</c>.</summary>
    internal static string SubtitleResAttributes(string? subtitleUrl)
        => subtitleUrl is null ? "" : $""" pv:subtitleFileType="SRT" pv:subtitleFileUri="{Xml(subtitleUrl)}" """.TrimEnd();

    /// <summary>The subtitle announcements next to the video <c>res</c>
    /// (namespaces <c>sec</c> / <c>pv</c> declared on DIDL-Lite).</summary>
    internal static string SubtitleElements(string? subtitleUrl)
        => subtitleUrl is null ? "" :
            $"""<sec:CaptionInfoEx sec:type="srt">{Xml(subtitleUrl)}</sec:CaptionInfoEx><sec:CaptionInfo sec:type="srt">{Xml(subtitleUrl)}</sec:CaptionInfo><res protocolInfo="http-get:*:text/srt:*">{Xml(subtitleUrl)}</res>""";

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    public void Dispose() { _scanLock.Dispose(); _fetchGate.Dispose(); }
}
