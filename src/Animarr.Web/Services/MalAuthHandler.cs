using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Delegating handler that injects the MAL Client-ID from AppConfig into every MAL request.
/// </summary>
public class MalAuthHandler(IAppConfigService appConfig) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clientId = await appConfig.GetAsync(AppConfigKeys.MalClientId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(clientId))
            request.Headers.TryAddWithoutValidation("X-MAL-CLIENT-ID", clientId);

        return await base.SendAsync(request, cancellationToken);
    }
}
