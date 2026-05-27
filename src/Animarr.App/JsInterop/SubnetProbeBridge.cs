using Animarr.App.Services;
using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

/// <summary>
/// JS-interop entry for the TCP-based subnet scan fallback. Used by the
/// Discovery page when mDNS returns empty — common on home WiFi setups where
/// the AP filters multicast between the wired server and wireless clients.
///
/// Same static-dispatch trick as <see cref="MdnsBridge"/> — we stash the
/// service singleton in <c>SubnetProbeService.Instance</c> (assigned by
/// MauiProgram) so the [JSInvokable] static method can reach it without
/// per-component DotNetObjectReference juggling.
/// </summary>
public static class SubnetProbeBridge
{
    /// <summary>Process-wide instance set from MauiProgram. Static so the
    /// JSInvokable below resolves without DotNetObjectReference. Idempotent.</summary>
    public static SubnetProbeService? Instance { get; private set; }
    public static void RegisterStaticInstance(SubnetProbeService svc) => Instance = svc;

    [JSInvokable("AnimarrSubnetProbe")]
    public static async Task<DiscoveredServer[]> ProbeAsync()
    {
        var svc = Instance;
        if (svc is null) return Array.Empty<DiscoveredServer>();
        return await svc.ProbeSubnetsAsync().ConfigureAwait(false);
    }
}
