using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// TV login via device pairing. Asks the server for a short code + QR
/// (<c>/api/auth/pair/init</c>), shows both, and polls
/// (<c>/api/auth/pair/poll</c>) every 2s until the phone confirms. The confirm
/// response carries Set-Cookie for the matching user, and because we poll on the
/// shared, cookie-jar HttpClient, our process is authenticated the moment it
/// flips — so we jump straight to the catalog. Codes expire; when one lapses we
/// transparently request a fresh one.
/// </summary>
public partial class PairingPage : ContentPage
{
    private readonly IAnimarrApiClient _api;
    private readonly ServerAddressProvider _addr;
    private CancellationTokenSource? _cts;

    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    public PairingPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _api  = services.GetRequiredService<IAnimarrApiClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        HintLabel.Text =
            $"Откройте {ImageBase} на телефоне или в браузере, войдите и введите код — " +
            "или отсканируйте QR камерой.";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PairInitDto init;
            try
            {
                init = await _api.InitPairAsync(new PairInitRequest("Android TV"), ct);
            }
            catch
            {
                StatusLabel.Text = "Не удалось получить код. Повтор через 3с…";
                try { await Task.Delay(3000, ct); } catch { return; }
                continue;
            }

            CodeLabel.Text = init.Code;
            QrImage.Source = $"{ImageBase}{ApiRoutes.PairQr}?code={Uri.EscapeDataString(init.Code)}";
            StatusLabel.Text = "Ожидание подтверждения…";

            // Poll until confirmed, or the code's TTL elapses (then re-init).
            while (!ct.IsCancellationRequested && DateTime.UtcNow < init.ExpiresAt)
            {
                try { await Task.Delay(2000, ct); } catch { return; }

                PairPollDto poll;
                try { poll = await _api.PollPairAsync(init.Code, ct); }
                catch { continue; }

                if (poll.Status == "confirmed")
                {
                    StatusLabel.Text = "Готово!";
                    TvRoot.Go(new CatalogNativePage());
                    return;
                }
                if (poll.Status == "expired") break;
            }
        }
    }
}
