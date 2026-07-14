using System.Windows.Input;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;

namespace Animarr.App;

/// <summary>
/// First onboarding stage: pick the Animarr server. Auto-discovers servers on
/// the LAN (mDNS/NSD + subnet probe) as focusable cards, and always offers a
/// manual address box for setups where discovery can't cross the AP boundary.
/// Any candidate is validated with an anonymous <c>GET /api/server/info</c>
/// probe before it's saved; then we branch to pairing or catalog by session.
/// D-pad activation runs through TapGestureRecognizer.Command (what
/// TvFocusBehavior invokes on OK) — not the Tapped event, which fires on touch
/// only.
/// </summary>
public partial class ServerPickerPage : ContentPage
{
    private readonly IAnimarrApiClient _api;
    private readonly ServerAddressProvider _addr;
    private readonly SubnetProbeService _subnet;
    private readonly MdnsBrowserService _mdns;
    private readonly HashSet<string> _seenIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _scanned;

    /// <summary>Bound to the "Подключиться" button so a D-pad OK activates it.</summary>
    public ICommand ConnectCommand { get; }

    public ServerPickerPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _api    = services.GetRequiredService<IAnimarrApiClient>();
        _addr   = services.GetRequiredService<ServerAddressProvider>();
        _subnet = services.GetRequiredService<SubnetProbeService>();
        _mdns   = services.GetRequiredService<MdnsBrowserService>();

        ConnectCommand = new Command(async () => await TryConnectAsync(UrlEntry.Text));

        // Pre-fill the last saved server (if any) for a one-click re-connect.
        var saved = Preferences.Get(TvRoot.ServerKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) UrlEntry.Text = saved;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_scanned) return;   // scan once per page instance
        _scanned = true;
        _ = ScanAsync();
    }

    // Race mDNS + subnet probe; cards appear as each source resolves. Both are
    // best-effort — a locked-down network just leaves the manual box.
    private async Task ScanAsync()
    {
        SetStatus("Поиск серверов в сети…");
        var a = AddResultsAsync(SafeProbeAsync());
        var b = AddResultsAsync(SafeBrowseAsync());
        await Task.WhenAll(a, b);
        if (!_busy)
            SetStatus(_seenIds.Count == 0 ? "Серверы не найдены — введите адрес вручную" : "");
    }

    private async Task<DiscoveredServer[]> SafeProbeAsync()
    {
        try { return await _subnet.ProbeSubnetsAsync(); }
        catch { return Array.Empty<DiscoveredServer>(); }
    }

    private async Task<DiscoveredServer[]> SafeBrowseAsync()
    {
        try { return await _mdns.BrowseAsync(); }
        catch { return Array.Empty<DiscoveredServer>(); }
    }

    private async Task AddResultsAsync(Task<DiscoveredServer[]> task)
    {
        var found = await task;
        foreach (var s in found)
        {
            var key = !string.IsNullOrWhiteSpace(s.ServerId) ? s.ServerId : s.BaseUrl;
            if (!_seenIds.Add(key)) continue;
            AddServerCard(s);
        }
    }

    private void AddServerCard(DiscoveredServer s)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label
                {
                    Text = string.IsNullOrWhiteSpace(s.Name) ? "Animarr" : s.Name,
                    TextColor = Colors.White, FontFamily = "Geist", FontSize = 17,
                },
                new Label
                {
                    Text = s.BaseUrl, TextColor = Color.FromArgb("#7b8290"),
                    FontFamily = "GeistMono", FontSize = 12,
                },
            },
        };
        var card = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#151a21"),
            Padding = new Thickness(18, 12),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = stack,
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 10 });
        var tap = new TapGestureRecognizer
        {
            Command = new Command(async () => await TryConnectAsync(s.BaseUrl)),
        };
        card.GestureRecognizers.Add(tap);
        FoundList.Children.Add(card);
    }

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
