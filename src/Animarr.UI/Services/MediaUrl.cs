using Animarr.Shared;

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
/// Authorization: every media endpoint needs the auth cookie or a signed
/// media token. A same-origin browser sends its cookie. Cross-origin loads —
/// the MAUI WebView's &lt;img&gt;/&lt;audio&gt; (SameSite keeps the cookie
/// off them), the TV's native image loader, ExoPlayer — can't, so when
/// <see cref="NeedsToken"/> is true every URL built here carries the user's
/// session token as <c>?mt=</c>. <see cref="EnsureTokenAsync"/> fetches and
/// refreshes it (GET /api/media-token, cookie-authenticated).
///
/// Subscribe to <see cref="OnBaseChanged"/> from image-rendering components
/// (Poster, Avatar, Hero) so they re-render when the active server flips or
/// the token rotates — without that, Blazor's parameter-diff skips them on a
/// parent StateHasChanged and the stale &lt;img src&gt; stays in the DOM.
/// </summary>
public static class MediaUrl
{
    private static string _base = "";
    private static string _pageOrigin = "";

    /// <summary>Fires whenever the base URL or the media token changes.
    /// Components that build image URLs in render-time getters subscribe to
    /// this and call StateHasChanged so the new URLs take effect without a
    /// full page reload.</summary>
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
        // A token is signed by ONE server — switching to another (or first
        // learning the real one) invalidates it; the next EnsureTokenAsync
        // fetches the new server's token.
        if (!string.IsNullOrEmpty(_base)) _token = null;
        _base = next;
        OnBaseChanged?.Invoke();
    }

    /// <summary>The page's own origin (NavigationManager.BaseUri). Lets
    /// <see cref="NeedsToken"/> tell a same-origin browser (cookie works) from
    /// a browser pointed at another server.</summary>
    public static void SetPageOrigin(string? pageUri)
    {
        if (string.IsNullOrWhiteSpace(pageUri)) return;
        try { _pageOrigin = new Uri(pageUri).GetLeftPart(UriPartial.Authority); }
        catch { /* keep the previous value */ }
    }

    // ─── Media token ─────────────────────────────────────────────────────

    private static string?        _token;
    private static Guid           _tokenUser;
    private static DateTimeOffset _tokenExpires;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Refresh once less than this is left. Tokens are issued for
    /// 6–12 h (bucketed server-side), so a refresh past this point gets the
    /// next bucket's token.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromHours(5);

    /// <summary>The current session media token, or null.</summary>
    public static string? Token => _token;

    /// <summary>The user the current token belongs to (null when none).</summary>
    public static Guid? TokenUserId => _token is null ? null : _tokenUser;

    /// <summary>True when URLs must carry the token: any non-browser host
    /// (the MAUI app — WebView and native pages) or a browser whose API base
    /// isn't its own origin. False for the plain same-origin web client, which
    /// keeps authenticating with its cookie.</summary>
    public static bool NeedsToken =>
        !OperatingSystem.IsBrowser()
        || (!string.IsNullOrEmpty(_base) && !string.IsNullOrEmpty(_pageOrigin)
            && !string.Equals(_base, _pageOrigin, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetch (or refresh) the session token when this host needs one.
    /// Cheap no-op while the cached token is fresh and belongs to
    /// <paramref name="userId"/> (pass the signed-in user's id so a profile
    /// switch picks up a new token). Never throws — on failure the previous
    /// token (if any) stays. Returns true when the token changed.</summary>
    public static async Task<bool> EnsureTokenAsync(
        IAnimarrApiClient api, Guid? userId = null, bool force = false, CancellationToken ct = default)
    {
        if (!NeedsToken) return false;
        if (!force && IsFresh(userId)) return false;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && IsFresh(userId)) return false;
            var dto = await api.GetMediaTokenAsync(ct);
            if (dto is null)
            {
                // Signed out (401) — drop the stale token.
                if (_token is null) return false;
                _token = null;
            }
            else
            {
                if (dto.Token == _token && dto.UserId == _tokenUser)
                {
                    _tokenExpires = dto.ExpiresAtUtc;
                    return false;
                }
                _token        = dto.Token;
                _tokenUser    = dto.UserId;
                _tokenExpires = dto.ExpiresAtUtc;
            }
        }
        catch
        {
            return false;   // offline / server hiccup — keep what we have
        }
        finally { _tokenLock.Release(); }

        OnBaseChanged?.Invoke();
        return true;
    }

    /// <summary>Forget the token (sign-out).</summary>
    public static void ClearToken()
    {
        if (_token is null) return;
        _token = null;
        OnBaseChanged?.Invoke();
    }

    private static bool IsFresh(Guid? userId) =>
        _token is not null
        && (userId is null || userId == _tokenUser)
        && _tokenExpires - DateTimeOffset.UtcNow > RefreshMargin;

    /// <summary>Append <c>mt=&lt;token&gt;</c> to <paramref name="url"/> when a
    /// token is held (regardless of <see cref="NeedsToken"/> — for native code
    /// that always talks cross-origin, e.g. the TV pages / ExoPlayer).</summary>
    public static string WithToken(string url)
    {
        var t = _token;
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(url)
            || url.Contains("?mt=", StringComparison.Ordinal)
            || url.Contains("&mt=", StringComparison.Ordinal))
            return url;
        return url + (url.Contains('?') ? '&' : '?') + "mt=" + Uri.EscapeDataString(t);
    }

    private static string Tokenize(string url) => NeedsToken ? WithToken(url) : url;

    // ─── Image widths (/api/image ?w=) ────────────────────────────────────

    /// <summary>Width for the full-screen backdrop layer behind
    /// <c>filter: blur(12–14px) brightness(38–50%)</c> (backdrop.js). Blurred
    /// that hard, 480 px is indistinguishable from the original fanart while
    /// decoding ~7–16× less texture memory (w1280 / 1920 sources) and
    /// transferring ~10× fewer bytes.</summary>
    public const int BlurredBackdropWidth = 480;

    // ─── URL builders ────────────────────────────────────────────────────

    /// <summary>Build an <c>/api/image</c> URL for the given absolute server
    /// path, with an optional <c>?t=</c> cache-buster.</summary>
    public static string Image(string path, long? cacheBuster = null, int? width = null)
    {
        var encoded = System.Uri.EscapeDataString(path);
        var query   = $"?path={encoded}";
        if (cacheBuster.HasValue) query += $"&t={cacheBuster.Value}";
        if (width.HasValue)       query += $"&w={width.Value}";
        return Tokenize(string.IsNullOrEmpty(_base)
            ? $"/api/image{query}"
            : $"{_base}/api/image{query}");
    }

    /// <summary>Rebase a server-relative URL (<c>/api/…</c>) onto the active
    /// server. DTOs like <c>CalendarEventDto</c> / rec cards / the trickplay
    /// manifest carry relative URLs — a browser resolves them against its
    /// origin, but the MAUI WebView's virtual host 404s them. Absolute
    /// (http/https) URLs pass through untouched; our own /api/ URLs get the
    /// media token when this host needs one.</summary>
    public static string Absolute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url!.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)) return url;
        var abs = string.IsNullOrEmpty(_base) ? url
            : url[0] == '/' ? _base + url : $"{_base}/{url}";
        return url.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? Tokenize(abs) : abs;
    }

    /// <summary>Build the <c>/api/media/{id}/theme</c> URL for a title's theme
    /// audio (anime OP/ED). Used as the <c>&lt;audio&gt;</c> src on the detail page.</summary>
    public static string Theme(System.Guid id)
        => Tokenize(string.IsNullOrEmpty(_base)
            ? $"/api/media/{id}/theme"
            : $"{_base}/api/media/{id}/theme");

    /// <summary>Per-episode thumbnail URL (TMDB still or an ffmpeg frame). Used
    /// as the &lt;EpisodeCard&gt; ThumbUrl so each episode shows a distinct image
    /// instead of the same season poster repeated. (It used to be routed
    /// through the MAUI loopback proxy so the proxy could attach the cookie;
    /// the media token replaces that.)</summary>
    public static string EpisodeThumb(System.Guid id, int season, int episode)
    {
        var path = $"/api/media/{id}/episode-thumb?season={season}&episode={episode}";
        return Tokenize(string.IsNullOrEmpty(_base) ? path : $"{_base}{path}");
    }
}
