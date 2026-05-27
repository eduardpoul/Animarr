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
///
/// Subscribe to <see cref="OnBaseChanged"/> from image-rendering components
/// (Poster, Avatar, Hero) so they re-render when the active server flips —
/// without that, Blazor's parameter-diff skips them on a parent
/// StateHasChanged and the stale &lt;img src&gt; stays in the DOM.
/// </summary>
public static class MediaUrl
{
    private static string _base = "";

    /// <summary>Fires whenever <see cref="SetBase"/> is called with a value
    /// different from the current base. Components that build image URLs in
    /// render-time getters subscribe to this and call StateHasChanged so the
    /// new base takes effect without a full page reload.</summary>
    public static event Action? OnBaseChanged;

    /// <summary>The currently-configured base URL (empty string = same-origin).
    /// Exposed for diagnostics / debugging.</summary>
    public static string Base => _base;

    /// <summary>Called once from <c>MainLayout.OnAfterRenderAsync</c> after the
    /// HttpClient base is known. Pass either an empty string (WASM origin —
    /// keeps URLs relative) or an absolute server URL (MAUI Hybrid).</summary>
    public static void SetBase(string? url)
    {
        var next = string.IsNullOrWhiteSpace(url) ? "" : url!.TrimEnd('/');
        if (string.Equals(next, _base, StringComparison.OrdinalIgnoreCase)) return;
        _base = next;
        OnBaseChanged?.Invoke();
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
