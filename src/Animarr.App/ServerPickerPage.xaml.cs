using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace Animarr.App;

/// <summary>
/// First onboarding stage: pick the Animarr server. Auto-discovers servers on
/// the LAN (mDNS/NSD + subnet probe) as focusable cards, and always offers a
/// manual address box for setups where discovery can't cross the AP boundary.
/// Any candidate is validated with an anonymous <c>GET /api/server/info</c>
/// probe before it's saved; then we branch to pairing or catalog by session.
/// </summary>
public partial class ServerPickerPage : ContentPage
{
    private readonly IAnimarrApiClient _api;
    private readonly ServerAddressProvider _addr;
    private bool _busy;

    public ServerPickerPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _api  = services.GetRequiredService<IAnimarrApiClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        // Pre-fill the last saved server (if any) for a one-click re-connect.
        var saved = Preferences.Get(TvRoot.ServerKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) UrlEntry.Text = saved;
    }

    private async void OnConnect(object? sender, EventArgs e)
        => await TryConnectAsync(UrlEntry.Text);

    private async Task TryConnectAsync(string? raw)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(raw))
        {
            SetStatus("Введите адрес сервера", warn: true);
            return;
        }

        var url = Normalize(raw);
        _busy = true;
        SetStatus("Проверяем сервер…");
        try
        {
            var info = await _api.GetServerInfoAsync(url);
            if (info is null)
            {
                SetStatus("Сервер не отвечает по этому адресу", warn: true);
                _busy = false;
                return;
            }

            Preferences.Set(TvRoot.ServerKey, url);
            _addr.Current = new Uri(url);
            SetStatus($"{info.Name} · {info.TitleCount} тайтлов");

            // Server picked — decide the next stage by session state.
            var status = await _api.GetAuthStatusAsync();
            TvRoot.Go(status.Authenticated ? new CatalogNativePage() : new PairingPage());
        }
        catch
        {
            SetStatus("Не удалось подключиться. Проверьте адрес.", warn: true);
            _busy = false;
        }
    }

    private void SetStatus(string text, bool warn = false)
    {
        StatusLabel.Text = text;
        StatusLabel.TextColor = warn ? Color.FromArgb("#e8a33d") : Color.FromArgb("#7b8290");
    }

    // Accept "host", "host:port" or a full URL — default to http:// for the
    // plain-HTTP LAN boxes that are the common self-hosted case.
    private static string Normalize(string raw)
    {
        raw = raw.Trim();
        if (!raw.Contains("://")) raw = "http://" + raw;
        return raw.TrimEnd('/');
    }
}
