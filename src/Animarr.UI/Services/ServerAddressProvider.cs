namespace Animarr.UI.Services;

/// <summary>
/// Mutable holder for the absolute base URL the shared <see cref="HttpClient"/>
/// should talk to right now. Backs the per-request URL rewrite done by
/// <see cref="ServerAddressHandler"/>.
///
/// Why a separate provider instead of just mutating <see cref="HttpClient.BaseAddress"/>:
/// HttpClient's `BaseAddress` can only be assigned BEFORE the first request — once
/// any extension method (GetFromJsonAsync, PostAsJsonAsync, …) has called
/// SendAsync, `PrepareRequestMessage` flips an internal "started" flag and the
/// setter throws `InvalidOperationException`. In MAUI the UserContextState
/// probe at app boot (/api/me) reliably races ahead of
/// ServerRegistryState.LoadAsync, which used to leave us with the
/// compile-time default origin baked in and no way to switch to the user's
/// chosen server without restarting the process.
///
/// Holding the live URL in this provider sidesteps the problem entirely: the
/// HttpClient is constructed once with a placeholder BaseAddress (only used to
/// satisfy `PrepareRequestMessage`'s relative-URI check), and the handler
/// rewrites the host of every outgoing request to <see cref="Current"/>.
///
/// Lifetime: singleton in MAUI (one HttpClient + one provider for the whole
/// process), scoped in WASM (one per circuit, matching HttpClient's lifetime
/// there). Either way it's a plain mutable holder with no threading guarantees
/// — Blazor's single-threaded execution model is enough.
/// </summary>
public sealed class ServerAddressProvider
{
    /// <summary>The base URL all relative /api/* calls should resolve against.
    /// Null means "no server chosen yet" — handler then leaves requests alone
    /// so the placeholder BaseAddress decides where they go (typically returns
    /// an ERR_ADDRESS_UNREACHABLE, which the catch-everything UI loaders
    /// handle gracefully).</summary>
    public Uri? Current { get; set; }
}
