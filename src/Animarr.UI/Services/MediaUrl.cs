namespace Animarr.UI.Services;

/// <summary>
/// Builds absolute API URLs for binary assets (images, thumbnails) so they
/// load correctly under MAUI Hybrid.
///
/// In WASM the bundle is served by the API host itself — a bare
/// <c>/api/image?path=…</c> img-src resolves to the same origin and works.
/// In MAUI Hybrid the BlazorWebView runs against a virtual host
/// (<c>https://0.0.0.0/</c> on Android, <c>https://0.0.0.1/</c> on Windows
/// WebView2). The HttpClient pipeline rewrites those for C# calls via
/// <c>ServerAddressHandler</c>, but <c>&lt;img src&gt;</c> and
/// <c>background-image: url(…)</c> bypass HttpClient entirely — the
/// WebView's own loader follows the virtual host and 404s every image.
///
/// Setting a base URL once at startup (from <c>HttpClient.BaseAddress</c>)
/// and routing every image src through <see cref="Image"/> makes the
/// markup target the real server regardless of platform.
/// </summary>
public static class MediaUrl
{
    private static string _base = "";

    /// <summary>Called once from <c>MainLayout.OnAfterRenderAsync</c> after the
    /// HttpClient base is known. Pass either an empty string (WASM origin —
    /// keeps URLs relative) or an absolute server URL (MAUI Hybrid).</summary>
    public static void SetBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { _base = ""; return; }
        _base = url.TrimEnd('/');
    }

    /// <summary>Build an <c>/api/image</c> URL for the given absolute server
    /// path, with an optional <c>?t=</c> cache-buster.</summary>
    public static string Image(string path, long? cacheBuster = null)
    {
        var encoded = System.Uri.EscapeDataString(path);
        var query   = cacheBuster.HasValue
            ? $"?path={encoded}&t={cacheBuster.Value}"
            : $"?path={encoded}";
        return string.IsNullOrEmpty(_base)
            ? $"/api/image{query}"
            : $"{_base}/api/image{query}";
    }
}
