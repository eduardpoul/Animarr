using Microsoft.AspNetCore.Authorization;

namespace Animarr.Web.Services.Auth;

/// <summary>
/// Authorization requirement that checks a single permission bit on the
/// current user's role. Backed by <see cref="PermissionAuthorizationHandler"/>
/// which hydrates the user through <see cref="IUserContext"/>.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(Func<Animarr.Web.Data.Models.Role, bool> selector)
    {
        Selector = selector;
    }

    public Func<Animarr.Web.Data.Models.Role, bool> Selector { get; }
}

/// <summary>
/// Resolves the current user (via <see cref="IUserContext"/>) and checks the
/// requirement's selector against their role. Cached per request, so
/// successive policies in the same request don't each hit the DB.
/// </summary>
public sealed class PermissionAuthorizationHandler
    : AuthorizationHandler<PermissionRequirement>
{
    private readonly IUserContext _userContext;

    public PermissionAuthorizationHandler(IUserContext userContext)
    {
        _userContext = userContext;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = await _userContext.GetCurrentUserAsync();
        if (user?.Role is not null && requirement.Selector(user.Role))
            context.Succeed(requirement);
    }
}
