namespace Animarr.Web.Services.Auth;

/// <summary>
/// Constants for the cookie auth scheme + claim names. Centralised so the
/// scheme name string (which appears in <c>[Authorize]</c> attributes,
/// <c>HttpContext.SignInAsync</c> calls and the cookie configuration) stays
/// in sync.
/// </summary>
public static class AuthConstants
{
    /// <summary>The single cookie auth scheme — there's no second auth method.</summary>
    public const string CookieScheme = "AnimarrCookie";

    /// <summary>Cookie name on the wire. Short to keep the request header small.</summary>
    public const string CookieName = "animarr_auth";

    /// <summary>Claim that carries the user GUID. Read by <c>UserContext</c>.</summary>
    public const string UserIdClaim = "uid";

    /// <summary>Claim that carries the role GUID. Cached on the cookie so a
    /// <c>/api/me</c> round-trip isn't required to gate the topbar UI.</summary>
    public const string RoleIdClaim = "rid";

    /// <summary>Convenience policy name for <c>[Authorize(Policy = ...)]</c>.</summary>
    public static class Policies
    {
        public const string ViewContent    = "perm:viewContent";
        public const string UploadContent  = "perm:uploadContent";
        public const string SystemSettings = "perm:systemSettings";
        public const string ManageUsers    = "perm:manageUsers";
    }
}

/// <summary>
/// Names of the rate-limiter policies registered in <c>Program.cs</c> and
/// referenced by endpoints via <c>RequireRateLimiting</c>. Kept next to
/// <see cref="AuthConstants"/> because both guard the anonymous surface.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Per-IP window for the anonymous TV-pairing endpoints — see
    /// the registration comment in <c>Program.cs</c> for the budget math.</summary>
    public const string Pair = "pair";
}
