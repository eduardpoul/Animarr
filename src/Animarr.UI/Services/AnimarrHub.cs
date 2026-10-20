using Microsoft.AspNetCore.SignalR.Client;

namespace Animarr.UI.Services;

/// <summary>
/// Builds the app's SignalR connections. Both hubs require a signed-in user:
/// the same-origin web client authenticates with its cookie, while the MAUI
/// app's HubConnection runs on its own .NET HTTP stack (no cookie jar) and
/// sends the session media token as its access token instead
/// (<see cref="MediaUrl.Token"/>; read on every connect / reconnect, so a
/// rotated token is picked up automatically).
/// </summary>
public static class AnimarrHub
{
    public static HubConnection Build(Uri hubUri)
    {
        var useToken = MediaUrl.NeedsToken;
        return new HubConnectionBuilder()
            .WithUrl(hubUri, o =>
            {
                if (useToken)
                    o.AccessTokenProvider = () => Task.FromResult(MediaUrl.Token);
            })
            .WithAutomaticReconnect(new HubRetryPolicy())
            .Build();
    }
}
