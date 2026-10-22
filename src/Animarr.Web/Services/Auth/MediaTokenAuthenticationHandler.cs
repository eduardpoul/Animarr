using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Names for the media-token auth surface. Kept apart from
/// <see cref="AuthConstants"/> so the cookie constants stay untouched.
///
/// Two schemes share one handler:
///   • <see cref="TokenScheme"/>   — session tokens AND per-file link tokens.
///     Only for the path-addressed byte endpoints, where
///     <see cref="MediaPathValidator"/> enforces a link's file binding. An
///     anonymous DLNA link opens only <see cref="DlnaLinkEndpoints"/>.
///   • <see cref="SessionScheme"/> — session tokens only. For everything else a
///     cookie-less client needs (id-addressed media endpoints, HLS keepalive,
///     DLNA cast control, SignalR hubs).
/// Neither is part of the default / fallback policy: a media token never
/// authorises the regular REST API.
/// </summary>
public static class MediaAuth
{
    public const string TokenScheme   = "AnimarrMediaToken";
    public const string SessionScheme = "AnimarrMediaSession";

    public static class Policies
    {
        /// <summary>Cookie, session token or a file-bound link token.</summary>
        public const string Media        = "media:any";
        /// <summary>Cookie or session token.</summary>
        public const string MediaSession = "media:session";
        /// <summary>Cookie or session token + the UploadContent permission
        /// (the torrent hub — same gate as the torrent REST group).</summary>
        public const string UploadLive   = "media:upload";
    }

    public static class Claims
    {
        /// <summary>The <see cref="MediaTokenKind"/> of the token, as a number.</summary>
        public const string Kind     = "mtk";
        /// <summary>Hex resource hash a link token is bound to.</summary>
        public const string Resource = "mres";
    }
}

public sealed class MediaTokenAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Accept per-file link tokens (kinds Link / DlnaLink) in addition
    /// to session tokens.</summary>
    public bool AllowLinks { get; set; }
}

/// <summary>
/// Authenticates a request from a media token carried in <c>?mt=</c>, the
/// <c>X-Media-Token</c> header, SignalR's <c>?access_token=</c> or an
/// <c>Authorization: Bearer</c> header. See <see cref="MediaTokenService"/> for
/// the token format.
///
/// A request that already carries a valid auth cookie is left to the cookie
/// (this handler answers NoResult), so a browser never ends up with two
/// identities merged into one principal.
/// </summary>
public sealed class MediaTokenAuthenticationHandler(
    IOptionsMonitor<MediaTokenAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MediaTokenService tokens,
    AuthService auth,
    DlnaService dlna)
    : AuthenticationHandler<MediaTokenAuthOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.User?.Identity?.IsAuthenticated == true)
            return AuthenticateResult.NoResult();

        var raw = ReadToken();
        if (string.IsNullOrEmpty(raw)) return AuthenticateResult.NoResult();

        if (!tokens.TryValidate(raw, out var claims) || claims is null)
            return AuthenticateResult.Fail("Invalid or expired media token.");

        if (claims.Kind != MediaTokenKind.Session && !Options.AllowLinks)
            return AuthenticateResult.Fail("A file link token can't be used here.");

        var list = new List<Claim>
        {
            new(MediaAuth.Claims.Kind, ((byte)claims.Kind).ToString()),
        };
        if (claims.ResourceHash is { } hash)
            list.Add(new Claim(MediaAuth.Claims.Resource, Convert.ToHexString(hash)));

        if (claims.Kind == MediaTokenKind.DlnaLink)
        {
            // Not tied to an account: honour it only on what a renderer fetches
            // from the browse answer, only while the admin keeps the DLNA
            // server on, and only from the LAN (the same gate as browse).
            if (!DlnaLinkEndpoints.Allows(Context))
                return AuthenticateResult.Fail("A DLNA link token only opens the DLNA byte endpoints.");
            if (!await dlna.IsServerEnabledAsync(Context.RequestAborted) || !LanRequest.IsLan(Context))
                return AuthenticateResult.Fail("DLNA server is off or the request isn't from the LAN.");
        }
        else
        {
            // Same cut-off as the cookie: a deleted account's tokens die with it.
            if (!await auth.UserExistsAsync(claims.UserId, Context.RequestAborted))
                return AuthenticateResult.Fail("Token user no longer exists.");
            list.Add(new Claim(AuthConstants.UserIdClaim, claims.UserId.ToString()));
        }

        var identity  = new ClaimsIdentity(list, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private string? ReadToken()
    {
        var q = Request.Query;
        if (q.TryGetValue(MediaTokenService.QueryParam, out var mt) && !string.IsNullOrEmpty(mt))
            return mt.ToString();
        var h = Request.Headers[MediaTokenService.HeaderName].ToString();
        if (!string.IsNullOrEmpty(h)) return h;
        // SignalR clients: browsers put the AccessTokenProvider value in
        // ?access_token= on the WebSocket URL; the .NET client sends a header.
        if (q.TryGetValue("access_token", out var at) && !string.IsNullOrEmpty(at))
            return at.ToString();
        var authz = Request.Headers.Authorization.ToString();
        if (authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authz["Bearer ".Length..].Trim();
        return null;
    }
}

/// <summary>
/// The endpoints a DLNA MediaServer link token (<see cref="MediaTokenKind.DlnaLink"/>)
/// may open — exactly the URLs the browse answer hands a renderer: the raw
/// file, the MPEG-TS remux and a sidecar's SRT captions. Everything else on the
/// media surface (HLS starts, analysis, fonts, failure reports …) refuses it:
/// the token is anonymous, and every library file gets one on the LAN.
/// Matched by the endpoint's route pattern, so a path's casing or a PathBase
/// can't widen it.
/// </summary>
public static class DlnaLinkEndpoints
{
    private static readonly HashSet<string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        Animarr.Shared.ApiRoutes.File,   // /api/file
        "/api/dlna/stream",
        "/api/subtitle/srt",
    };

    public static bool Allows(HttpContext http)
        => http.GetEndpoint() is Microsoft.AspNetCore.Routing.RouteEndpoint e
           && e.RoutePattern.RawText is string raw
           && Routes.Contains("/" + raw.TrimStart('/'));
}

/// <summary>"Is this request from the local network?" — direct connection
/// from a loopback / private / link-local address, with no reverse-proxy
/// forwarding headers (a proxy on the same box would otherwise make internet
/// traffic look local).</summary>
public static class LanRequest
{
    public static bool IsLan(HttpContext http)
    {
        var h = http.Request.Headers;
        if (h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP"))
            return false;
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        // IPv6: unique-local fc00::/7 and link-local fe80::/10.
        if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal) return true;
        return false;
    }
}
