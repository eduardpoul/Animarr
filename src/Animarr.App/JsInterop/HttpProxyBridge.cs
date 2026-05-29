using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

/// <summary>
/// JS-callable HTTP proxy that lets the BlazorWebView reach a plain-HTTP
/// Animarr server despite the WebView's HTTPS virtual host.
///
/// Why this exists (and why it's not a WebViewClient):
///   MAUI BlazorWebView mounts the Razor bundle at https://0.0.0.x/. Chromium
///   then blocks every fetch/XHR to http://LAN-ip as "active mixed content".
///   The obvious fix — a custom Android WebViewClient.ShouldInterceptRequest —
///   doesn't stick: MAUI re-installs its OWN WebViewClient after ours (it needs
///   it to serve the bundle from the virtual host), so our client is dropped.
///   The reliable channel is plain JS↔.NET interop, which always works. The
///   JS fetch/XHR wrappers in animarr-player.js call ProxyFetch for any
///   http:// URL; we run the request through native HttpClient (no
///   mixed-content rules) and hand the bytes back base64-encoded.
///
/// Cost: each proxied response is base64'd over the interop bridge (+33%
/// size). Fine for the JSON control calls (start/probe/keepalive) and
/// acceptable for HLS segments on a LAN. On the native (ExoPlayer) path the
/// segments don't come through here at all — ExoPlayer fetches them with its
/// own HTTP stack — so this hot path only matters for the web (hls.js) player.
/// </summary>
public static class HttpProxyBridge
{
    // Reused across calls; HttpClient is designed to be shared. No cookie
    // container needed — the player endpoints are AllowAnonymous and the
    // auth cookie rides on the Blazor-side IAnimarrApiClient, not these
    // media fetches.
    private static readonly System.Net.Http.HttpClient _http = new()
    {
        Timeout = System.TimeSpan.FromSeconds(60),  // HLS segments on slow LAN
    };

    /// <summary>
    /// Fetch <paramref name="url"/> via native HttpClient and return the
    /// response as a base64 body + status + content-type. Headers passed from
    /// JS are forwarded (minus the ones HttpClient owns).
    /// </summary>
    [JSInvokable("ProxyFetch")]
    public static async Task<ProxyResponse> ProxyFetchAsync(
        string url, string method,
        System.Collections.Generic.Dictionary<string, string>? headers,
        string? body)
    {
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                new System.Net.Http.HttpMethod(string.IsNullOrEmpty(method) ? "GET" : method),
                url);

            if (!string.IsNullOrEmpty(body))
                req.Content = new System.Net.Http.StringContent(
                    body, System.Text.Encoding.UTF8, "application/json");

            if (headers is not null)
            {
                foreach (var kvp in headers)
                {
                    if (string.Equals(kvp.Key, "Content-Type", System.StringComparison.OrdinalIgnoreCase))
                        continue;  // set on the StringContent above
                    if (string.Equals(kvp.Key, "Content-Length", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(kvp.Key, "Host", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { req.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value); } catch { }
                }
            }

            using var resp = await _http.SendAsync(req,
                System.Net.Http.HttpCompletionOption.ResponseContentRead);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var mime  = resp.Content.Headers.ContentType?.ToString()
                        ?? "application/octet-stream";

            return new ProxyResponse(
                (int)resp.StatusCode,
                mime,
                System.Convert.ToBase64String(bytes));
        }
        catch (System.Exception ex)
        {
            // Status 0 signals a transport failure to the JS side (mirrors
            // fetch()'s TypeError / XHR's error event semantics).
            return new ProxyResponse(0, "text/plain",
                System.Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(ex.Message)));
        }
    }
}

/// <summary>Flat proxied-response payload marshalled back to JS. Body is
/// base64 so binary (HLS segments) survives the string-only interop bridge.</summary>
public sealed record ProxyResponse(int Status, string ContentType, string BodyBase64);
