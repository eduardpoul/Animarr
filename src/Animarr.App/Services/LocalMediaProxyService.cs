using System.Net;
using System.Net.Sockets;
using Animarr.UI.Services;

namespace Animarr.App.Services;

/// <summary>
/// A tiny loopback HTTP reverse-proxy that lets the BlazorWebView reach a
/// plain-HTTP Animarr server WITHOUT tripping Chromium's mixed-content gate —
/// and without the base64 JS↔.NET bridge that froze playback.
///
/// Why this exists
/// ───────────────
/// MAUI mounts the Razor bundle at <c>https://0.0.0.1/</c>. From that HTTPS
/// page, a fetch/XHR to <c>http://&lt;lan-ip&gt;:8080</c> is "active mixed
/// content" and Chromium blocks it. The old workaround proxied every request
/// (incl. each HLS segment) through a JSInvokable as base64 — the GC churn from
/// those multi-MB strings froze the picture.
///
/// The fix leans on a spec detail: Chromium treats <c>127.0.0.1</c> / localhost
/// as a "potentially trustworthy" origin, so a fetch from the HTTPS page to
/// <c>http://127.0.0.1:&lt;port&gt;</c> is NOT mixed content and is allowed.
/// So we run this proxy on loopback; hls.js talks to it over a normal socket
/// (native streaming, no base64, no GC storm), and we forward every request to
/// the real LAN server. Result: the ordinary web player works smoothly, the
/// HUD overlays the video natively (it's all one WebView layer), and the
/// self-hoster does NOTHING — no certs, no server-side HTTPS.
///
/// The target server is read live from <see cref="ServerAddressProvider"/>, so
/// switching servers needs no proxy restart.
/// </summary>
public sealed class LocalMediaProxyService
{
    private readonly ServerAddressProvider _addr;

    // One shared client; no cookies (player endpoints are AllowAnonymous, auth
    // rides the Blazor-side IAnimarrApiClient). No auto-redirect so we can pass
    // 3xx straight back to hls.js. Generous timeout for HLS segments on slow LAN.
    private static readonly HttpClient s_http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies        = false,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private HttpListener? _listener;

    /// <summary>Loopback port the proxy is listening on (0 until Start()).</summary>
    public int Port { get; private set; }

    /// <summary>Full loopback base URL, e.g. <c>http://127.0.0.1:49xxx</c>.
    /// This is what the WebView's apiBase points at.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static LocalMediaProxyService? Instance { get; private set; }
    public static void RegisterStaticInstance(LocalMediaProxyService svc) => Instance = svc;

    public LocalMediaProxyService(ServerAddressProvider addr) => _addr = addr;

    /// <summary>Bind a free loopback port and start serving. Idempotent.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        try
        {
            Port = GetFreeLoopbackPort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
#if ANDROID
            Android.Util.Log.Info("Animarr.Proxy", $"Local media proxy listening on {BaseUrl}");
#endif
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("Animarr.Proxy", $"Failed to start local proxy: {ex}");
#endif
            _listener = null;
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }  // listener stopped/disposed
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            // CORS preflight — the page origin (https://0.0.0.1) differs from
            // the proxy origin (http://127.0.0.1), so hls.js' XHR is a CORS
            // request. Answer OPTIONS without forwarding.
            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                AddCors(resp);
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            var serverBase = _addr.Current;
            if (serverBase is null)
            {
                resp.StatusCode = 503;
                resp.Close();
                return;
            }

            // RawUrl is the path+query exactly as the WebView requested it; map
            // it onto the real server's origin.
            var target = new Uri(serverBase, req.RawUrl ?? "/");
            using var fwd = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);

            // Copy request body (POST/PUT — e.g. /api/dlna/play).
            if (req.HasEntityBody)
            {
                var buffered = new MemoryStream();
                await req.InputStream.CopyToAsync(buffered).ConfigureAwait(false);
                buffered.Position = 0;
                fwd.Content = new StreamContent(buffered);
            }

            // Forward request headers (incl. Range, so seeking → 206 works).
            // Host is rewritten by the target Uri; content headers go on Content.
            foreach (string? key in req.Headers.AllKeys)
            {
                if (key is null) continue;
                if (IsHopByHop(key) || key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = req.Headers[key];
                if (value is null) continue;
                if (!fwd.Headers.TryAddWithoutValidation(key, value))
                    fwd.Content?.Headers.TryAddWithoutValidation(key, value);
            }

            using var upstream = await s_http
                .SendAsync(fwd, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            resp.StatusCode = (int)upstream.StatusCode;

            // Copy response headers, skipping the ones HttpListener owns.
            CopyResponseHeaders(upstream.Headers, resp);
            CopyResponseHeaders(upstream.Content.Headers, resp);
            AddCors(resp);

            var len = upstream.Content.Headers.ContentLength;
            if (len.HasValue) resp.ContentLength64 = len.Value;
            else              resp.SendChunked = true;

            // Stream with a large buffer (fewer syscalls → better throughput for
            // big 4K segments) and time it, so logcat shows the effective
            // per-segment Mbps. If a segment transfers fast here but playback
            // still hitches, the bottleneck is network/decode (content too heavy
            // for the device), not the proxy.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = 0;
            await using (var body = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var buf = new byte[256 * 1024];
                int n;
                while ((n = await body.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await resp.OutputStream.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    total += n;
                }
            }
            sw.Stop();
#if ANDROID
            // Log EVERY proxied request so we can see exactly what the WebView
            // pulls through the proxy (manifest / segments / direct-play file)
            // and the throughput. If playback happens with NO lines here, the
            // segments are bypassing the proxy (e.g. still hitting the base64
            // bridge) — which would explain residual GC/freeze.
            double mbps = total > 0 ? total * 8.0 / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds) : 0;
            Android.Util.Log.Info("Animarr.Proxy",
                $"{req.HttpMethod} {req.Url?.AbsolutePath} -> {resp.StatusCode} " +
                $"{total / 1024}KB {sw.ElapsedMilliseconds}ms {mbps:F1}Mbps");
#endif
        }
        catch (Exception ex)
        {
            try { resp.StatusCode = 502; } catch { }
#if ANDROID
            Android.Util.Log.Warn("Animarr.Proxy", $"proxy {req.RawUrl}: {ex.Message}");
#endif
        }
        finally
        {
            try { resp.OutputStream.Close(); } catch { }
            try { resp.Close(); } catch { }
        }
    }

    private static void AddCors(HttpListenerResponse resp)
    {
        try
        {
            resp.Headers["Access-Control-Allow-Origin"]  = "*";
            resp.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
            resp.Headers["Access-Control-Allow-Headers"] = "*";
        }
        catch { }
    }

    private static void CopyResponseHeaders(System.Net.Http.Headers.HttpHeaders src, HttpListenerResponse dst)
    {
        foreach (var h in src)
        {
            if (IsHopByHop(h.Key)) continue;
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // set via ContentLength64
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                dst.ContentType = string.Join(",", h.Value);
                continue;
            }
            try { dst.Headers[h.Key] = string.Join(",", h.Value); } catch { /* restricted header — skip */ }
        }
    }

    private static bool IsHopByHop(string key) =>
        key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
}
