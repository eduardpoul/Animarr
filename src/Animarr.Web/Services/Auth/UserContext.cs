using System.Security.Claims;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Auth;

/// <inheritdoc cref="IUserContext"/>
public sealed class UserContext : IUserContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private User? _cached;
    private bool  _resolved;

    public UserContext(IHttpContextAccessor http, IDbContextFactory<AppDbContext> dbFactory)
    {
        _http      = http;
        _dbFactory = dbFactory;
    }

    public Guid? CurrentUserId
    {
        get
        {
            var claim = _http.HttpContext?.User?.FindFirst(AuthConstants.UserIdClaim)?.Value;
            return Guid.TryParse(claim, out var g) ? g : null;
        }
    }

    public Guid? CurrentRoleId
    {
        get
        {
            var claim = _http.HttpContext?.User?.FindFirst(AuthConstants.RoleIdClaim)?.Value;
            return Guid.TryParse(claim, out var g) ? g : null;
        }
    }

    public bool IsAuthenticated => CurrentUserId.HasValue;

    public async ValueTask<User?> GetCurrentUserAsync(CancellationToken ct = default)
    {
        if (_resolved) return _cached;

        var uid = CurrentUserId;
        if (uid is null)
        {
            _resolved = true;
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _cached = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == uid.Value, ct);
        _resolved = true;
        return _cached;
    }
}
