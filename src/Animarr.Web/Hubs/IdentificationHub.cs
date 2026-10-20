using Animarr.Web.Data;
using Animarr.Web.Services.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Hubs;

/// <summary>
/// SignalR hub for the identification queue + media-item status updates.
/// Subscribers (NeedsReview chip, Settings → Identification log, MediaDetail's
/// in-flight indicator) get pushed events instead of polling.
///
/// Like <see cref="TorrentHub"/>, this is push-only — server forwards events
/// from <see cref="IdentificationQueueProcessorService"/> via the broadcaster.
///
/// Signed-in connections only (Program.cs maps it behind the media-session
/// policy). Broadcasts are routed per audience instead of Clients.All, so a
/// folder-restricted profile never learns about titles outside its folders:
///   • <see cref="AdminGroup"/>  — SystemSettings users: full queue entries;
///   • <see cref="ViewerGroup"/> — everyone else: a payload-less "queue changed"
///     ping (their UI only uses it as a refetch trigger);
///   • <see cref="RoleGroup"/>   — per role: MediaItemChanged only for items the
///     role can see, NeedsReviewCountChanged with the role-filtered count.
/// Membership is computed on connect, from the user's current role.
/// </summary>
public sealed class IdentificationHub(IDbContextFactory<AppDbContext> dbFactory) : Hub
{
    public const string AdminGroup  = "ident:admin";
    public const string ViewerGroup = "ident:viewer";
    public static string RoleGroup(Guid roleId) => "ident:role:" + roleId.ToString("N");

    public override async Task OnConnectedAsync()
    {
        // Context.User is the principal the hub's policy authenticated (cookie
        // or session media token). IHttpContextAccessor isn't reliable inside
        // hubs, so read the claim here and load the role directly.
        Animarr.Web.Data.Models.User? me = null;
        if (Guid.TryParse(Context.User?.FindFirst(AuthConstants.UserIdClaim)?.Value, out var uid))
        {
            await using var db = await dbFactory.CreateDbContextAsync(Context.ConnectionAborted);
            me = await db.Users.AsNoTracking().Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == uid, Context.ConnectionAborted);
        }
        if (me?.Role is null)
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId,
            me.Role.PermSystemSettings ? AdminGroup : ViewerGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoleGroup(me.Role.Id));
        await base.OnConnectedAsync();
    }
}
