using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Injects the TMDB API key into every request.
/// - v3 key (32 hex chars): appended as ?api_key= query parameter
/// - v4 Read Access Token (JWT starting with "eyJ"): sent as Authorization: Bearer
/// </summary>
public class TmdbAuthHandler(IAppConfigService appConfig) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = await appConfig.GetTmdbApiKeyAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
            {
                // v4 Read Access Token (JWT) — use Bearer
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                // v3 API key — append as query parameter
                var uriBuilder = new UriBuilder(request.RequestUri!);
                var sep = string.IsNullOrEmpty(uriBuilder.Query) ? "" : "&";
                uriBuilder.Query = uriBuilder.Query.TrimStart('?') + sep + "api_key=" + Uri.EscapeDataString(apiKey);
                request.RequestUri = uriBuilder.Uri;
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
