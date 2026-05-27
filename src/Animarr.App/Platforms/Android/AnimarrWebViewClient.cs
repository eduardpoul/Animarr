#if ANDROID
using Android.Webkit;

namespace Animarr.App.Platforms.Android;

/// <summary>
/// Custom <see cref="WebViewClient"/> that intercepts <c>http://</c> image
/// (and other static asset) requests issued from the WebView's HTTPS bundle
/// and refetches them server-side, returning the bytes through the
/// <c>shouldInterceptRequest</c> hook. This turns what Chromium otherwise
/// flags as "Mixed Content" (HTTPS page → HTTP subresource) into a
/// same-process load that bypasses the mixed-content gate entirely.
///
/// Background — why this is needed:
///
/// MAUI BlazorWebView mounts the Blazor bundle at <c>https://0.0.0.0/</c>
/// (Android) or <c>https://0.0.0.1/</c> (Windows) using a virtual scheme.
/// Animarr's self-hosted media server, by contrast, runs on plain HTTP
/// against a LAN IP like <c>http://192.168.11.200:8080</c>. The browser's
/// Mixed Content policy refuses to load HTTP subresources from an HTTPS
/// page even when:
///   • <c>android:usesCleartextTraffic="true"</c> is in the manifest
///   • <c>network_security_config</c> has cleartextTrafficPermitted="true"
///   • <c>WebSettings.MixedContentMode = MIXED_CONTENT_ALWAYS_ALLOW</c>
///
/// All three are set in this app — and Mixed Content errors still flooded
/// the console because Chromium WebView's mixed-content gate is enforced at
/// a layer above WebSettings (probably the renderer process). The ONLY
/// reliable workaround on a stock Chromium WebView is to satisfy the load
/// ourselves: when the page asks for <c>http://server/api/image</c>, we
/// return the bytes synchronously from our own HTTP fetch, and the
/// renderer never sees a cleartext URL fly past it.
///
/// Trade-off: each image goes through our process instead of the WebView's
/// native fetch pipeline, so we lose the WebView's disk + memory cache.
/// We compensate with the server's own <c>image-cache</c> directory on
/// the same LAN; the fetch is RAM-fast and adds ~5-15ms per image. Fine
/// for posters/fanart which are sized in tens of KB.
/// </summary>
internal sealed class AnimarrWebViewClient : WebViewClient
{
    private static readonly System.Net.Http.HttpClient _http = new()
    {
        Timeout = System.TimeSpan.FromSeconds(8),
    };

    public override WebResourceResponse? ShouldInterceptRequest(
        global::Android.Webkit.WebView? view,
        IWebResourceRequest? request)
    {
        try
        {
            var uri = request?.Url?.ToString();
            if (string.IsNullOrEmpty(uri)) return null;

            // Only intercept plain HTTP. HTTPS goes through the WebView's
            // own fetch — no mixed-content drama there. Bundle assets under
            // https://0.0.0.x/ also go straight through.
            if (!uri.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase))
                return null;

            // Refetch through our own HttpClient. We pass through method +
            // headers from the original request so the server sees the same
            // payload it would have served the WebView. .Result is OK here
            // because shouldInterceptRequest runs on a dedicated worker
            // thread (not the UI thread) — blocking is the API's contract.
            using var req = new System.Net.Http.HttpRequestMessage
            {
                RequestUri = new System.Uri(uri, System.UriKind.Absolute),
                Method     = new System.Net.Http.HttpMethod(request?.Method ?? "GET"),
            };
            if (request?.RequestHeaders is not null)
            {
                foreach (var kvp in request.RequestHeaders)
                {
                    // Skip headers HttpClient owns — Content-Type goes on the body,
                    // Host gets recomputed, etc.
                    if (string.Equals(kvp.Key, "Host", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kvp.Key, "Content-Length", System.StringComparison.OrdinalIgnoreCase)) continue;
                    try { req.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value); } catch { }
                }
            }

            var resp = _http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();

            // Pick a sane content-type. Most of what we proxy is an image
            // or a JSON payload — both arrive with the right header from
            // the server already.
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var encoding = resp.Content.Headers.ContentType?.CharSet;

            // Buffer the body. Streaming a network stream straight into the
            // WebResourceResponse hangs occasionally on slow LAN frames; the
            // RAM cost is bounded by HttpClient's response size and we're
            // talking poster JPEGs / small JSON.
            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var ms    = new System.IO.MemoryStream(bytes);

            var headers = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var h in resp.Headers)        headers[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            // Force a CORS-permissive response so the WebView treats the
            // bytes as same-origin friendly. We're the only consumer.
            headers["Access-Control-Allow-Origin"] = "*";

            return new WebResourceResponse(
                mime,
                encoding,
                (int)resp.StatusCode,
                resp.ReasonPhrase ?? "OK",
                headers,
                ms);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.WebView",
                $"ShouldInterceptRequest fail: {ex.GetType().Name}: {ex.Message}");
            // Returning null lets the WebView fall back to its native
            // network stack — which will then hit the Mixed Content gate
            // and visibly break the image. At least the UI doesn't hang.
            return null;
        }
    }
}
#endif
