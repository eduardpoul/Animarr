using System.Net;

namespace Animarr.App.Services;

/// <summary>
/// <see cref="DelegatingHandler"/> that snapshots the shared
/// <see cref="CookieContainer"/> to disk every time a response includes a
/// <c>Set-Cookie</c> header. Pairs with <see cref="CookiePersistence.Load"/>
/// at app boot — together they keep the user signed in across MAUI app
/// restarts.
///
/// Sits BEFORE <c>HttpClientHandler</c> in the pipeline (so the inner
/// handler has already merged the new cookies into the container by the
/// time we observe the response). The cost is one File.WriteAllText per
/// login / refresh — negligible.
/// </summary>
public sealed class CookiePersistHandler : DelegatingHandler
{
    private readonly CookieContainer _container;

    public CookiePersistHandler(CookieContainer container)
    {
        _container = container;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        // Persist when the server set, refreshed or cleared a cookie. We
        // check the response header rather than walking the container so a
        // plain GET that doesn't touch cookies doesn't trigger a disk write.
        if (response.Headers.Contains("Set-Cookie"))
        {
            CookiePersistence.Save(_container);
        }
        return response;
    }
}
