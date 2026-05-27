using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.UI;

/// <summary>
/// DI registration helpers for Animarr.UI. Each host (Animarr.Web.Client
/// WASM, Animarr.App MAUI Hybrid, Animarr.Web during transition) calls
/// these in its <c>builder.Services</c> setup.
///
/// Animarr.UI itself doesn't depend on a concrete HttpClient or hub URL —
/// the host wires those up. We just make sure <see cref="IAnimarrApiClient"/>
/// is registered the canonical way every consumer expects.
/// </summary>
public static class AnimarrUiServiceCollectionExtensions
{
    /// <summary>
    /// Register an HTTP-backed <see cref="IAnimarrApiClient"/> against the
    /// host's pre-configured <c>HttpClient</c>. The host is responsible for
    /// adding an <c>HttpClient</c> with the right <c>BaseAddress</c> — for
    /// the WASM client that's the page origin; for MAUI it's the user's
    /// configured server URL.
    /// </summary>
    public static IServiceCollection AddAnimarrApiClient(this IServiceCollection services)
    {
        services.AddScoped<IAnimarrApiClient>(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            return new HttpAnimarrApiClient(http);
        });
        return services;
    }

    /// <summary>
    /// Register the v4 UI plumbing — UserContextState (auth + preferences cache)
    /// and FolderJumpService (catalog folder selection that lives above the
    /// route in TopBar). Both are <see cref="ServiceLifetime.Scoped"/> — one
    /// per circuit on Blazor Server, one per app instance on WASM/MAUI.
    /// </summary>
    public static IServiceCollection AddAnimarrUiState(this IServiceCollection services)
    {
        services.AddScoped<UserContextState>();
        services.AddScoped<FolderJumpService>();
        // v5: category-strip selection state shared between TopBar (writer) and
        // Home.razor (reader). Replaces FolderJumpService for the primary
        // catalog filter; FolderJumpService stays for admin flows that still
        // want folder-level navigation.
        services.AddScoped<CategoryNavService>();
        // v5 multi-server: registry of known Animarr servers. Scoped — same
        // lifetime envelope as UserContextState; one per circuit/app instance.
        services.AddScoped<ServerRegistryState>();
        return services;
    }
}
