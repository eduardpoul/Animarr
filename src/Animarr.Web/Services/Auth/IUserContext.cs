using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Scoped accessor that resolves the current authenticated user from the
/// HTTP request's cookie. Returns null when the request is anonymous —
/// every endpoint that needs the user should call <see cref="GetCurrentUserAsync"/>
/// and treat null as "not signed in".
///
/// Internally backed by <c>HttpContext.User</c> claims; the actual User
/// record is fetched lazily through <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{T}"/>
/// when the endpoint asks for it, so requests that don't need the user data
/// (just the id from claims) don't pay for a DB round-trip.
/// </summary>
public interface IUserContext
{
    /// <summary>User GUID from the cookie claims, or null if anonymous.</summary>
    Guid? CurrentUserId { get; }

    /// <summary>Role GUID from the cookie claims, or null if anonymous.</summary>
    Guid? CurrentRoleId { get; }

    /// <summary>True iff <see cref="CurrentUserId"/> is set.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Fully hydrate the current user record (including Role) from
    /// the database. Cached per request — multiple calls within one request
    /// share the same User instance. Returns null if anonymous OR the cookie
    /// references a user that no longer exists (e.g. admin deleted them while
    /// they were signed in — cookie becomes inert).</summary>
    ValueTask<User?> GetCurrentUserAsync(CancellationToken ct = default);
}
