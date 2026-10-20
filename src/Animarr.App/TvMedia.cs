using System.Net;
using Animarr.Shared;
using Animarr.UI.Services;

namespace Animarr.App;

/// <summary>
/// Media-token plumbing for the native (XAML) TV pages and the ExoPlayer
/// player page. Their poster / backdrop <c>Image.Source</c> URLs are fetched
/// by MAUI's own image loader and their stream URLs by ExoPlayer — neither
/// carries the auth cookie that the app's HttpClient holds, and every media
/// endpoint now requires authorization. So every such URL gets the user's
/// session media token appended (<c>?mt=</c>), shared with the Blazor side
/// through <see cref="MediaUrl"/>.
/// </summary>
internal static class TvMedia
{
    private static string? _cookieSeen;

    /// <summary>Make sure a fresh token for the signed-in user is held. Cheap
    /// when it is (no request). A changed auth cookie (sign-in, pairing,
    /// profile switch) forces a re-fetch so the token follows the account —
    /// the server returns the same token for the same user within its 6-hour
    /// window, so a mere cookie renewal costs one tiny request and changes no
    /// URL.</summary>
    public static async Task EnsureTokenAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        var api = services.GetService<IAnimarrApiClient>();
        if (api is null) return;

        string? cookie = null;
        try
        {
            var jar  = services.GetService<CookieContainer>();
            var addr = services.GetService<ServerAddressProvider>()?.Current;
            if (jar is not null && addr is not null) cookie = jar.GetCookieHeader(addr);
        }
        catch { /* no jar / no server yet */ }

        var force = !string.Equals(cookie, _cookieSeen, StringComparison.Ordinal);
        await MediaUrl.EnsureTokenAsync(api, force: force);
        _cookieSeen = cookie;
    }

    /// <summary>Append the media token to a server URL (absolute or relative).</summary>
    public static string Url(string url) => MediaUrl.WithToken(url);
}
