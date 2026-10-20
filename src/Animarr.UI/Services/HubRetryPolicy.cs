using Microsoft.AspNetCore.SignalR.Client;

namespace Animarr.UI.Services;

/// <summary>
/// Reconnect schedule for the live hubs (downloads badge, AI queue, torrents
/// list, needs-review chip, folder stats). The default WithAutomaticReconnect()
/// tries at 0/2/10/30 s and then gives up for good — a server redeploy or
/// container restart longer than ~42 s left every open tab and the phone app
/// showing frozen counters until a manual reload. Keep trying, backing off to
/// every 30 s.
/// </summary>
public sealed class HubRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Ramp =
    [
        TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
    ];

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
        => retryContext.PreviousRetryCount < Ramp.Length
            ? Ramp[retryContext.PreviousRetryCount]
            : TimeSpan.FromSeconds(30);
}
