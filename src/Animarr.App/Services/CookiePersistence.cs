using System.Net;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Animarr.App.Services;

/// <summary>
/// Persists the auth <see cref="CookieContainer"/> to a JSON file in
/// <see cref="FileSystem.AppDataDirectory"/> so the user stays signed in
/// across MAUI app restarts. Without this the in-memory CookieContainer
/// is wiped on every launch → <c>/api/me</c> returns 401 → the app
/// bounces to <c>/welcome</c> even though the server is still reachable.
///
/// Strategy
/// ────────
/// • <see cref="Load"/> runs once at MauiProgram bootstrap and re-hydrates
///   every saved cookie back into the shared container before the first
///   HttpClient request fires.
/// • <see cref="Save"/> is called from <see cref="CookiePersistHandler"/>
///   after every response that carried a <c>Set-Cookie</c> header — login,
///   logout, refresh, etc. — so the file always reflects the live state.
///
/// The cookies in our case (AnimarrCookie, ASP.NET data-protected) are
/// platform-bound, but storing the raw HTTP cookie string still works
/// because the protector key lives on the *server*; the client just
/// echoes the cookie back unmodified.
/// </summary>
public static class CookiePersistence
{
    /// <summary>Path to the saved-cookies JSON. Uses <see cref="FileSystem.AppDataDirectory"/>
    /// which is per-app sandboxed on every MAUI target (LocalAppData on
    /// Windows, app-private dir on Android/iOS/Mac).</summary>
    public static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "auth-cookies.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Read the JSON file (if present) and re-add every saved cookie
    /// to <paramref name="container"/>. Failures (missing file, malformed
    /// JSON, expired cookies) are swallowed — worst case the user is
    /// asked to sign in again.</summary>
    public static void Load(CookieContainer container)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var items = JsonSerializer.Deserialize<CookieRecord[]>(json, JsonOpts);
            if (items is null) return;

            foreach (var r in items)
            {
                if (string.IsNullOrEmpty(r.Name) || string.IsNullOrEmpty(r.Domain))
                    continue;
                // Skip expired cookies — they'd be dead weight in the container
                // and could trigger "expired" auth bounces server-side anyway.
                if (r.Expires is { } e && e < DateTime.UtcNow) continue;

                var cookie = new Cookie(r.Name, r.Value ?? "", r.Path, r.Domain)
                {
                    HttpOnly = r.HttpOnly,
                    Secure   = r.Secure,
                };
                if (r.Expires is { } exp) cookie.Expires = exp;
                container.Add(cookie);
            }
        }
        catch
        {
            // Corrupted file — drop it so the next save starts fresh.
            try { File.Delete(FilePath); } catch { }
        }
    }

    /// <summary>Serialise every cookie in <paramref name="container"/> back
    /// to the JSON file. Called from the DelegatingHandler whenever a
    /// response carries Set-Cookie, so the file mirrors the in-memory
    /// container with minimal latency.</summary>
    public static void Save(CookieContainer container)
    {
        try
        {
            var items = container.GetAllCookies()
                .Where(c => !c.Expired)
                .Select(c => new CookieRecord
                {
                    Name     = c.Name,
                    Value    = c.Value,
                    Path     = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    Domain   = c.Domain,
                    Expires  = c.Expires == DateTime.MinValue ? null : c.Expires,
                    HttpOnly = c.HttpOnly,
                    Secure   = c.Secure,
                })
                .ToArray();

            var json = JsonSerializer.Serialize(items, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Disk full / permissions / temp lock — losing one save just
            // means the next login round-trips; not worth crashing for.
        }
    }

    /// <summary>Mirror the container's cookies into the Android WebView's
    /// CookieManager. The auth cookie lives in the app's HttpClient pipeline,
    /// but <c>&lt;img&gt;</c> / media requests issued by the BlazorWebView go
    /// through the WEBVIEW's network stack, which has its own cookie store —
    /// without this sync, cookie-gated binary endpoints (e.g. the older
    /// servers' <c>/api/media/{id}/episode-thumb</c>) return 401 and the
    /// thumbnails render broken on phones. Call after <see cref="Load"/> at
    /// boot and after every Set-Cookie (login / refresh / logout).</summary>
    public static void SyncToWebView(CookieContainer container)
    {
#if ANDROID
        try
        {
            var mgr = global::Android.Webkit.CookieManager.Instance;
            if (mgr is null) return;
            mgr.SetAcceptCookie(true);
            foreach (var c in container.GetAllCookies().Where(c => !c.Expired))
            {
                var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
                var url  = $"http{(c.Secure ? "s" : "")}://{c.Domain}{path}";
                mgr.SetCookie(url, $"{c.Name}={c.Value}; Path={path}");
            }
            mgr.Flush();
        }
        catch { /* WebView not ready yet — the next sync will land */ }
#endif
    }

    /// <summary>Stable serialisation shape. Doesn't include attributes the
    /// .NET <see cref="Cookie"/> doesn't round-trip cleanly (SameSite,
    /// Discard) — they're inferred by the server side anyway.</summary>
    private sealed record CookieRecord
    {
        public string  Name     { get; init; } = "";
        public string  Value    { get; init; } = "";
        public string  Path     { get; init; } = "/";
        public string  Domain   { get; init; } = "";
        public DateTime? Expires { get; init; }
        public bool    HttpOnly { get; init; }
        public bool    Secure   { get; init; }
    }
}
