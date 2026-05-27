namespace Animarr.UI.Services;

/// <summary>
/// <see cref="DelegatingHandler"/> that rewrites every outgoing request's host
/// to <see cref="ServerAddressProvider.Current"/> just before it goes over the
/// wire. Used by both MAUI and WASM hosts so swapping the active Animarr
/// server (multi-server discovery, ProfilePanel "Switch server", initial
/// hydrate from localStorage) takes effect immediately without the
/// `HttpClient.BaseAddress` "already started" lock.
///
/// The HttpClient itself keeps a static (and otherwise meaningless)
/// BaseAddress so the framework's relative-URI check passes; this handler
/// runs after `PrepareRequestMessage` has composed BaseAddress + path into
/// an absolute Uri, and replaces the authority before forwarding.
/// </summary>
public sealed class ServerAddressHandler : DelegatingHandler
{
    private readonly ServerAddressProvider _provider;

    public ServerAddressHandler(ServerAddressProvider provider)
    {
        _provider = provider;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Rewrite(request);
        return base.SendAsync(request, ct);
    }

    /// <summary>Synchronous overload — kept in case any consumer goes through
    /// the legacy sync path (the JSON extensions don't, but WebAssembly's
    /// transport may differ across runtimes).</summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        Rewrite(request);
        return base.Send(request, ct);
    }

    private void Rewrite(HttpRequestMessage request)
    {
        var current = _provider.Current;
        if (current is null) return;
        if (request.RequestUri is null) return;
        if (!request.RequestUri.IsAbsoluteUri) return; // PrepareRequestMessage always absolutises before us

        // Already pointing at the right authority — nothing to do. Cheap
        // string-compare; avoids re-allocating Uri instances on every request.
        if (string.Equals(
                request.RequestUri.GetLeftPart(UriPartial.Authority),
                current.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return;

        // Splice: keep path + query + fragment, swap scheme + authority.
        var authority = new Uri(current.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        request.RequestUri = new Uri(authority, request.RequestUri.PathAndQuery + request.RequestUri.Fragment);
    }
}
