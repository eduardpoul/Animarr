using Animarr.App.Services;
using Microsoft.JSInterop;

namespace Animarr.App.JsInterop;

/// <summary>
/// Static JS-interop entry point so the Blazor JS layer can call into the
/// running MAUI process to enumerate <c>_animarr._tcp.local</c> instances.
///
/// Why static: <see cref="JSInvokableAttribute"/> on an instance method
/// requires the JS caller to first acquire a <c>DotNetObjectReference</c>
/// from a Blazor component and call <c>invokeMethodAsync</c> through that.
/// That works inside a single component lifetime but cross-page nav makes
/// the reference setup/teardown tedious — every page that wants to scan
/// would need its own DI-aware bridge component.
///
/// Instead we keep the singleton <see cref="MdnsBrowserService"/> reachable
/// from anywhere via <c>MdnsBrowserService.Instance</c> (assigned in
/// <c>MauiProgram.CreateMauiApp</c>) and expose a single static method here.
/// JS calls
/// <c>DotNet.invokeMethodAsync('Animarr.App', 'AnimarrMdnsBrowse')</c>
/// from anywhere — no component coupling, no lifecycle worries.
/// </summary>
public static class MdnsBridge
{
    [JSInvokable("AnimarrMdnsBrowse")]
    public static async Task<DiscoveredServer[]> BrowseAsync()
    {
        var svc = MdnsBrowserService.Instance;
        if (svc is null)
        {
            // Static dispatch ran before MauiProgram wired the singleton.
            // Shouldn't happen in normal flow (Discovery is invoked from
            // within the running app) but return empty rather than throw so
            // the JS side can fall through to manual-add gracefully.
            return Array.Empty<DiscoveredServer>();
        }
        return await svc.BrowseAsync().ConfigureAwait(false);
    }
}
