using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Minimal UPnP / DLNA MediaServer.
///
/// Lets Smart TVs (Samsung Tizen, LG webOS, …), VLC, Kodi, Infuse and similar
/// LAN clients **discover** Animarr without configuration and **browse** the
/// library natively — they then pull the bytes from <c>/api/video</c> at full
/// device-native quality (HEVC / DV / Atmos passthrough), bypassing the
/// browser-side transcode entirely.
///
/// What's implemented (just enough to satisfy real-world clients):
///   • SSDP advertiser: NOTIFY ssdp:alive on bootstrap, periodic re-announce,
///     ssdp:byebye on shutdown. Responds to M-SEARCH for root-device and
///     MediaServer service types.
///   • HTTP endpoints (mounted at /dlna/* in Program.cs):
///       GET  /dlna/desc.xml         — device description
///       GET  /dlna/cd.xml           — ContentDirectory SCPD
///       GET  /dlna/cm.xml           — ConnectionManager SCPD
///       POST /dlna/cd/control       — Browse / Search SOAP actions
///       POST /dlna/cm/control       — GetProtocolInfo SOAP action
///       GET/SUBSCRIBE /dlna/event/* — event stubs (200 OK, no events sent)
///
/// What's NOT implemented:
///   • DLNA media classes beyond videoItem / videoBroadcast — every entry
///     reports as "object.item.videoItem" which all major clients accept.
///   • Event subscriptions — many TVs don't subscribe at all; the ones that
///     do tolerate a stub. If something acts up we'll fill this in.
///   • Per-section access control — Animarr is single-user no-auth by design,
///     so the DLNA surface mirrors the catalog as-is.
///
/// **Deployment note:** SSDP needs LAN multicast (UDP 239.255.255.250:1900).
/// Docker bridge networking does NOT pass multicast through. Run animarr
/// with <c>--network host</c> for DLNA discovery to work; otherwise the
/// HTTP endpoints still respond but no TV will find them automatically.
/// </summary>
public sealed class DlnaService : IHostedService, IDisposable
{
    private const string SsdpAddr  = "239.255.255.250";
    private const int    SsdpPort  = 1900;
    private const string ServerSig = "Animarr/0.3 UPnP/1.0 DLNADOC/1.50";

    private readonly ILogger<DlnaService>   _logger;
    private readonly IServiceScopeFactory   _scopes;
    private readonly IConfiguration         _config;
    // Optional ControlPoint side — when DI provides it, every NOTIFY frame
    // we receive on the multicast socket gets forwarded so the renderer
    // cache stays warm without us having to poll.
    private DlnaCastService?                _cast;

    private UdpClient?   _udp;
    private Timer?       _aliveTimer;
    private CancellationTokenSource? _cts;
    private string       _uuid     = Guid.NewGuid().ToString();
    private string?      _localIp;
    private int          _httpPort = 8080;  // overridden in StartAsync if needed
    private bool         _enabled  = true;

    public DlnaService(ILogger<DlnaService> logger, IServiceScopeFactory scopes, IConfiguration config,
        DlnaCastService? cast = null)
    {
        _logger = logger;
        _scopes = scopes;
        _config = config;
        _cast   = cast;
    }

    public string Uuid     => _uuid;
    public string FriendlyName => "Animarr";
    /// <summary>The IP/port the SSDP LOCATION header advertises. Cast targets
    /// must use this exact origin so a TV can reach the stream URL we hand it
    /// — using the Host header from the browser would point at hostnames the
    /// TV can't resolve, or wrong ports when accessed via Caddy on 8443.</summary>
    public string? AdvertisedHost => _localIp is null ? null : $"http://{_localIp}:{_httpPort}";

    // ─── Lifecycle ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        // Toggle via env: DLNA_ENABLED=false to skip entirely.
        var en = _config["DLNA_ENABLED"] ?? Environment.GetEnvironmentVariable("DLNA_ENABLED");
        if (en is not null && bool.TryParse(en, out var b) && !b) { _enabled = false; return Task.CompletedTask; }

        // Stable UUID per machine — TVs cache by uuid and re-discovery hates churn.
        var stableSeed = (_config["DLNA_UUID"] ?? Environment.MachineName + "_animarr");
        _uuid = MakeStableUuid(stableSeed);

        // Two ways to set the advertised host: env var (essential when running
        // in Docker bridge mode — auto-detected IP would be 172.17.x.y which
        // TVs can't reach) OR auto-detect from local NICs (works in host
        // networking, dev on bare metal, macOS native).
        _localIp = _config["DLNA_ADVERTISE_IP"]
                ?? Environment.GetEnvironmentVariable("DLNA_ADVERTISE_IP")
                ?? PickLocalIp();
        if (_localIp is null)
        {
            _logger.LogWarning("DLNA: couldn't determine local LAN IP — discovery disabled.");
            _enabled = false;
            return Task.CompletedTask;
        }

        // Port: env override > ASPNETCORE_URLS port > default 8080.
        if (int.TryParse(_config["DLNA_ADVERTISE_PORT"]
                      ?? Environment.GetEnvironmentVariable("DLNA_ADVERTISE_PORT"), out var advPort))
        {
            _httpPort = advPort;
        }
        else
        {
            var urls = _config["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (!string.IsNullOrEmpty(urls))
            {
                foreach (var u in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Uri.TryCreate(u.Replace("+", _localIp), UriKind.Absolute, out var uri) && uri.Port > 0)
                    {
                        _httpPort = uri.Port; break;
                    }
                }
            }
        }

        _cts = new CancellationTokenSource();
        try
        {
            // Reuse so multiple listeners can co-exist (we share the box with
            // a real OS that may also have SSDP-aware daemons).
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            _udp.JoinMulticastGroup(IPAddress.Parse(SsdpAddr), IPAddress.Parse(_localIp));
            _udp.MulticastLoopback = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLNA: SSDP socket setup failed — most likely Docker bridge networking (need --network host). HTTP endpoints will still respond.");
            _udp?.Dispose();
            _udp = null;
            // Continue without SSDP — HTTP endpoints still serve, and clients
            // pointed at descUrl directly can still browse.
            return Task.CompletedTask;
        }

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        SendAlive();
        // Re-announce every ~25 minutes (we say cache-control: max-age=1800).
        _aliveTimer = new Timer(_ => SendAlive(), null, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(25));

        _logger.LogInformation("DLNA: MediaServer up at http://{Ip}:{Port}/dlna/desc.xml (uuid={Uuid})",
            _localIp, _httpPort, _uuid);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!_enabled) return Task.CompletedTask;
        _aliveTimer?.Dispose();
        try { if (_udp is not null) SendByeBye(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose() { _aliveTimer?.Dispose(); _udp?.Dispose(); _cts?.Dispose(); }

    // ─── SSDP ───────────────────────────────────────────────────────────────

    private string DescUrl => $"http://{_localIp}:{_httpPort}/dlna/desc.xml";

    private void SendAlive()
    {
        if (_udp is null) return;
        SendNotify("ssdp:alive", "upnp:rootdevice",                                   $"uuid:{_uuid}::upnp:rootdevice");
        SendNotify("ssdp:alive", $"uuid:{_uuid}",                                     $"uuid:{_uuid}");
        SendNotify("ssdp:alive", "urn:schemas-upnp-org:device:MediaServer:1",         $"uuid:{_uuid}::urn:schemas-upnp-org:device:MediaServer:1");
        SendNotify("ssdp:alive", "urn:schemas-upnp-org:service:ContentDirectory:1",   $"uuid:{_uuid}::urn:schemas-upnp-org:service:ContentDirectory:1");
        SendNotify("ssdp:alive", "urn:schemas-upnp-org:service:ConnectionManager:1",  $"uuid:{_uuid}::urn:schemas-upnp-org:service:ConnectionManager:1");
    }

    private void SendByeBye()
    {
        SendNotify("ssdp:byebye", "upnp:rootdevice",                                  $"uuid:{_uuid}::upnp:rootdevice");
        SendNotify("ssdp:byebye", $"uuid:{_uuid}",                                    $"uuid:{_uuid}");
        SendNotify("ssdp:byebye", "urn:schemas-upnp-org:device:MediaServer:1",        $"uuid:{_uuid}::urn:schemas-upnp-org:device:MediaServer:1");
        SendNotify("ssdp:byebye", "urn:schemas-upnp-org:service:ContentDirectory:1",  $"uuid:{_uuid}::urn:schemas-upnp-org:service:ContentDirectory:1");
        SendNotify("ssdp:byebye", "urn:schemas-upnp-org:service:ConnectionManager:1", $"uuid:{_uuid}::urn:schemas-upnp-org:service:ConnectionManager:1");
    }

    private void SendNotify(string nts, string nt, string usn)
    {
        if (_udp is null) return;
        var sb = new StringBuilder();
        sb.Append("NOTIFY * HTTP/1.1\r\n");
        sb.Append($"HOST: {SsdpAddr}:{SsdpPort}\r\n");
        sb.Append($"CACHE-CONTROL: max-age=1800\r\n");
        sb.Append($"LOCATION: {DescUrl}\r\n");
        sb.Append($"NT: {nt}\r\n");
        sb.Append($"NTS: {nts}\r\n");
        sb.Append($"SERVER: {ServerSig}\r\n");
        sb.Append($"USN: {usn}\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        try { _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(SsdpAddr), SsdpPort)); }
        catch (Exception ex) { _logger.LogDebug(ex, "SSDP send {Nt} failed", nt); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var res = await _udp.ReceiveAsync(ct);
                var text = Encoding.ASCII.GetString(res.Buffer);
                if (text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
                    HandleMSearch(text, res.RemoteEndPoint);
                else if (text.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
                {
                    // Forward NOTIFY frames to the ControlPoint so renderers
                    // that announce themselves (ssdp:alive on bootstrap, periodic
                    // re-announce) populate the cast cache without an active scan.
                    if (_cast is not null) _ = _cast.IngestNotifyAsync(text, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "SSDP receive error"); }
        }
    }

    private void HandleMSearch(string packet, IPEndPoint from)
    {
        // Parse ST header — only respond to types we implement.
        string? st = null;
        foreach (var line in packet.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
            { st = l[3..].Trim(); break; }
        }
        if (st is null) return;

        // ssdp:all = client wants everything. Some clients send specific types.
        var matches = st switch
        {
            "ssdp:all"                                            => true,
            "upnp:rootdevice"                                     => true,
            "urn:schemas-upnp-org:device:MediaServer:1"           => true,
            "urn:schemas-upnp-org:service:ContentDirectory:1"     => true,
            "urn:schemas-upnp-org:service:ConnectionManager:1"    => true,
            _ when st == $"uuid:{_uuid}"                          => true,
            _                                                     => false,
        };
        if (!matches) return;

        // Respond with each matching pair (M-SEARCH responses are unicast).
        if (st is "ssdp:all" or "upnp:rootdevice")
            SendSearchResponse(from, "upnp:rootdevice",                                  $"uuid:{_uuid}::upnp:rootdevice");
        if (st is "ssdp:all" || st == $"uuid:{_uuid}")
            SendSearchResponse(from, $"uuid:{_uuid}",                                    $"uuid:{_uuid}");
        if (st is "ssdp:all" or "urn:schemas-upnp-org:device:MediaServer:1")
            SendSearchResponse(from, "urn:schemas-upnp-org:device:MediaServer:1",        $"uuid:{_uuid}::urn:schemas-upnp-org:device:MediaServer:1");
        if (st is "ssdp:all" or "urn:schemas-upnp-org:service:ContentDirectory:1")
            SendSearchResponse(from, "urn:schemas-upnp-org:service:ContentDirectory:1",  $"uuid:{_uuid}::urn:schemas-upnp-org:service:ContentDirectory:1");
        if (st is "ssdp:all" or "urn:schemas-upnp-org:service:ConnectionManager:1")
            SendSearchResponse(from, "urn:schemas-upnp-org:service:ConnectionManager:1", $"uuid:{_uuid}::urn:schemas-upnp-org:service:ConnectionManager:1");
    }

    private void SendSearchResponse(IPEndPoint to, string st, string usn)
    {
        if (_udp is null) return;
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("CACHE-CONTROL: max-age=1800\r\n");
        sb.Append($"DATE: {DateTime.UtcNow:R}\r\n");
        sb.Append("EXT:\r\n");
        sb.Append($"LOCATION: {DescUrl}\r\n");
        sb.Append($"SERVER: {ServerSig}\r\n");
        sb.Append($"ST: {st}\r\n");
        sb.Append($"USN: {usn}\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        try { _udp.Send(bytes, bytes.Length, to); }
        catch (Exception ex) { _logger.LogDebug(ex, "SSDP M-SEARCH response failed"); }
    }

    // ─── Device description ─────────────────────────────────────────────────

    public string GetDeviceDescriptionXml(string baseUrl)
    {
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
            <friendlyName>{Xml(FriendlyName)}</friendlyName>
            <manufacturer>Animarr</manufacturer>
            <manufacturerURL>https://github.com/eduardpoul/animarr</manufacturerURL>
            <modelDescription>Animarr self-hosted media library</modelDescription>
            <modelName>Animarr</modelName>
            <modelNumber>0.3</modelNumber>
            <UDN>uuid:{_uuid}</UDN>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
                <SCPDURL>/dlna/cd.xml</SCPDURL>
                <controlURL>/dlna/cd/control</controlURL>
                <eventSubURL>/dlna/cd/event</eventSubURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                <SCPDURL>/dlna/cm.xml</SCPDURL>
                <controlURL>/dlna/cm/control</controlURL>
                <eventSubURL>/dlna/cm/event</eventSubURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;
    }

    public string GetContentDirectoryScpd() => """
    <?xml version="1.0" encoding="utf-8"?>
    <scpd xmlns="urn:schemas-upnp-org:service-1-0">
      <specVersion><major>1</major><minor>0</minor></specVersion>
      <actionList>
        <action><name>Browse</name>
          <argumentList>
            <argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>
            <argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument>
            <argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>
            <argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>
            <argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
            <argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>
            <argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>
            <argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
            <argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
            <argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument>
          </argumentList>
        </action>
        <action><name>GetSearchCapabilities</name>
          <argumentList>
            <argument><name>SearchCaps</name><direction>out</direction><relatedStateVariable>SearchCapabilities</relatedStateVariable></argument>
          </argumentList>
        </action>
        <action><name>GetSortCapabilities</name>
          <argumentList>
            <argument><name>SortCaps</name><direction>out</direction><relatedStateVariable>SortCapabilities</relatedStateVariable></argument>
          </argumentList>
        </action>
        <action><name>GetSystemUpdateID</name>
          <argumentList>
            <argument><name>Id</name><direction>out</direction><relatedStateVariable>SystemUpdateID</relatedStateVariable></argument>
          </argumentList>
        </action>
      </actionList>
      <serviceStateTable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType>
          <allowedValueList><allowedValue>BrowseMetadata</allowedValue><allowedValue>BrowseDirectChildren</allowedValue></allowedValueList>
        </stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>SearchCapabilities</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="no"><name>SortCapabilities</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="yes"><name>SystemUpdateID</name><dataType>ui4</dataType></stateVariable>
      </serviceStateTable>
    </scpd>
    """;

    public string GetConnectionManagerScpd() => """
    <?xml version="1.0" encoding="utf-8"?>
    <scpd xmlns="urn:schemas-upnp-org:service-1-0">
      <specVersion><major>1</major><minor>0</minor></specVersion>
      <actionList>
        <action><name>GetProtocolInfo</name>
          <argumentList>
            <argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument>
            <argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument>
          </argumentList>
        </action>
      </actionList>
      <serviceStateTable>
        <stateVariable sendEvents="yes"><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="yes"><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable>
        <stateVariable sendEvents="yes"><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>
      </serviceStateTable>
    </scpd>
    """;

    // ─── SOAP action handlers ───────────────────────────────────────────────

    public async Task<string> HandleContentDirectoryAsync(string soapXml, string baseUrl)
    {
        var doc = new XmlDocument();
        doc.LoadXml(soapXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("s",  "http://schemas.xmlsoap.org/soap/envelope/");
        ns.AddNamespace("u",  "urn:schemas-upnp-org:service:ContentDirectory:1");

        var action = doc.SelectSingleNode("//s:Body/u:*", ns) ?? doc.SelectSingleNode("//s:Body/*", ns);
        if (action is null) return SoapFault("Invalid request");

        var name = action.LocalName;
        return name switch
        {
            "Browse"               => await HandleBrowseAsync(action, baseUrl),
            "GetSearchCapabilities" => SoapEnvelope("GetSearchCapabilitiesResponse", "ContentDirectory:1",
                                          "<SearchCaps></SearchCaps>"),
            "GetSortCapabilities"  => SoapEnvelope("GetSortCapabilitiesResponse",   "ContentDirectory:1",
                                          "<SortCaps></SortCaps>"),
            "GetSystemUpdateID"    => SoapEnvelope("GetSystemUpdateIDResponse",     "ContentDirectory:1",
                                          "<Id>1</Id>"),
            _                      => SoapFault($"Unknown action: {name}"),
        };
    }

    public string HandleConnectionManager(string soapXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(soapXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("s",  "http://schemas.xmlsoap.org/soap/envelope/");

        var action = doc.SelectSingleNode("//s:Body/*", ns);
        if (action is null) return SoapFault("Invalid request");

        return action.LocalName switch
        {
            // Advertise the full set of formats /api/file can serve raw.
            // Smart TVs match these against their decoder capabilities to
            // decide whether they can play an item without server-side
            // help; missing entries can cause the TV to skip / grey-out
            // perfectly playable content.
            "GetProtocolInfo" => SoapEnvelope("GetProtocolInfoResponse", "ConnectionManager:1",
                "<Source>http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/quicktime:*,http-get:*:video/x-m4v:*,http-get:*:video/webm:*,http-get:*:video/x-msvideo:*,http-get:*:video/mp2t:*,http-get:*:video/x-ms-wmv:*,http-get:*:video/x-flv:*,http-get:*:video/ogg:*,http-get:*:video/mpeg:*</Source><Sink></Sink>"),
            _ => SoapFault($"Unknown action: {action.LocalName}"),
        };
    }

    // ─── ContentDirectory.Browse — the core ────────────────────────────────

    private async Task<string> HandleBrowseAsync(XmlNode action, string baseUrl)
    {
        var objectId   = action["ObjectID"]?.InnerText        ?? "0";
        var browseFlag = action["BrowseFlag"]?.InnerText       ?? "BrowseDirectChildren";
        var startIdx   = int.TryParse(action["StartingIndex"]?.InnerText, out var si) ? si : 0;
        var reqCount   = int.TryParse(action["RequestedCount"]?.InnerText, out var rc) ? rc : 0;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var didl = new StringBuilder();
        didl.Append(@"<DIDL-Lite xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"" xmlns:dc=""http://purl.org/dc/elements/1.1/"" xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/"" xmlns:dlna=""urn:schemas-dlna-org:metadata-1-0/"">");

        int total = 0;
        int returned = 0;

        if (objectId is "0" or "")
        {
            // Root → one container per section.
            var sections = await ctx.FolderWatchers
                .AsNoTracking()
                .Where(f => f.IsSection)
                .OrderBy(f => f.Label)
                .ToListAsync();
            total = sections.Count;

            if (browseFlag == "BrowseMetadata")
            {
                didl.Append(Container("0", "-1", "Animarr", total));
                returned = 1; total = 1;
            }
            else
            {
                var slice = ApplyRange(sections, startIdx, reqCount).ToList();
                foreach (var s in slice)
                {
                    var childCount = await ctx.MediaItems.CountAsync(m => m.Folder!.ParentSectionId == s.Id || m.FolderId == s.Id);
                    didl.Append(Container($"section-{s.Id}", "0", s.Label, childCount));
                }
                returned = slice.Count;
            }
        }
        else if (objectId.StartsWith("section-"))
        {
            // Section → flat list of media items (movies as items, series as items
            // pointing at the season-1 episode-1 file, or whatever single file we
            // have — quick-and-dirty MVP, TV-fans will tweak this once we wire
            // episode browsing).
            var sidStr = objectId["section-".Length..];
            if (!Guid.TryParse(sidStr, out var sid)) return SoapFault("Bad ObjectID");

            var items = await ctx.MediaItems
                .AsNoTracking()
                .Include(m => m.Folder)
                .Where(m => m.Folder!.ParentSectionId == sid || m.Folder!.Id == sid)
                .OrderBy(m => m.Title)
                .ToListAsync();
            total = items.Count;

            if (browseFlag == "BrowseMetadata")
            {
                didl.Append(Container(objectId, "0", "Section", total));
                returned = 1; total = 1;
            }
            else
            {
                var slice = ApplyRange(items, startIdx, reqCount).ToList();
                foreach (var m in slice)
                {
                    var file = ResolvePlayablePath(m);
                    if (file is null) continue;
                    didl.Append(VideoItem(
                        id:        $"media-{m.Id}",
                        parentId:  objectId,
                        title:     m.Title ?? Path.GetFileNameWithoutExtension(file),
                        filePath:  file,
                        baseUrl:   baseUrl));
                    returned++;
                }
            }
        }
        else if (objectId.StartsWith("media-"))
        {
            if (!Guid.TryParse(objectId["media-".Length..], out var mid)) return SoapFault("Bad ObjectID");
            var m = await ctx.MediaItems.AsNoTracking().Include(x => x.Folder).FirstOrDefaultAsync(x => x.Id == mid);
            if (m is null) return SoapFault("Not found");
            var file = ResolvePlayablePath(m);
            if (file is null) return SoapFault("No playable file");
            // Single-item browse.
            didl.Append(VideoItem(objectId, "0", m.Title ?? Path.GetFileNameWithoutExtension(file), file, baseUrl));
            returned = 1; total = 1;
        }
        else
        {
            return SoapFault("Unknown ObjectID");
        }

        didl.Append("</DIDL-Lite>");

        return SoapEnvelope("BrowseResponse", "ContentDirectory:1", $"""
            <Result>{Xml(didl.ToString())}</Result>
            <NumberReturned>{returned}</NumberReturned>
            <TotalMatches>{total}</TotalMatches>
            <UpdateID>1</UpdateID>
        """);
    }

    // ─── DIDL-Lite helpers ──────────────────────────────────────────────────

    private static string Container(string id, string parent, string title, int childCount)
        => $"""<container id="{Xml(id)}" parentID="{Xml(parent)}" restricted="1" childCount="{childCount}"><dc:title>{Xml(title)}</dc:title><upnp:class>object.container</upnp:class></container>""";

    private static string VideoItem(string id, string parentId, string title, string filePath, string baseUrl)
    {
        // /api/file serves the source file byte-for-byte with Range support.
        // No transcode → TVs decode HEVC / DV / Atmos with their own
        // hardware, 5.1 surround intact, no Animarr CPU cost. Browser-only
        // clients still hit /api/video for re-mux; DLNA clients (which is
        // what reads this DIDL) always handle the source codec natively.
        var streamUrl = $"{baseUrl}/api/file?path={Uri.EscapeDataString(filePath)}";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".mp4"  => "video/mp4",
            ".m4v"  => "video/x-m4v",
            ".mov"  => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv"  => "video/x-matroska",
            ".avi"  => "video/x-msvideo",
            ".ts"   => "video/mp2t",
            ".m2ts" => "video/mp2t",
            ".wmv"  => "video/x-ms-wmv",
            _       => "video/mp4",
        };
        // DLNA.ORG_PN/SEEK/BYTE flags — bare-minimum profile that real clients accept.
        // We don't claim a specific profile (let the TV guess) but advertise byte-seek.
        var proto = $"http-get:*:{mime}:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        return $"""<item id="{Xml(id)}" parentID="{Xml(parentId)}" restricted="1"><dc:title>{Xml(title)}</dc:title><upnp:class>object.item.videoItem</upnp:class><res protocolInfo="{Xml(proto)}">{Xml(streamUrl)}</res></item>""";
    }

    // ─── Path resolution per media item ────────────────────────────────────

    private static string? ResolvePlayablePath(MediaItem m)
    {
        var folder = m.Folder;
        if (folder is null) return null;

        // Flat single-file entry (movie).
        if (!string.IsNullOrEmpty(folder.SingleFilePath) && File.Exists(folder.SingleFilePath))
            return folder.SingleFilePath;

        // Per-title folder → look for the first video file we can find.
        if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
        {
            try
            {
                var first = Directory
                    .EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                return first;
            }
            catch { return null; }
        }
        return null;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".flv", ".ts", ".m2ts", ".ogv",
    };

    // ─── SOAP plumbing ──────────────────────────────────────────────────────

    private static string SoapEnvelope(string responseName, string serviceVersion, string body)
        => $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:{responseName} xmlns:u="urn:schemas-upnp-org:service:{serviceVersion}">{body}</u:{responseName}></s:Body></s:Envelope>""";

    private static string SoapFault(string detail)
        => $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>402</errorCode><errorDescription>{Xml(detail)}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""";

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static IEnumerable<T> ApplyRange<T>(IEnumerable<T> src, int start, int count)
    {
        if (start > 0) src = src.Skip(start);
        if (count > 0) src = src.Take(count);
        return src;
    }

    // ─── Networking helpers ─────────────────────────────────────────────────

    private static string? PickLocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = addr.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;     // link-local
                    return s;
                }
            }
        }
        catch { }
        return null;
    }

    private static string MakeStableUuid(string seed)
    {
        // Deterministic UUID v5-ish: SHA1(seed) → take 16 bytes → format as GUID.
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        var guid = new Guid(bytes.Take(16).ToArray());
        return guid.ToString();
    }
}
