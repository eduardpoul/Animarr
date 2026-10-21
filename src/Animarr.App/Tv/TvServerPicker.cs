#if ANDROID
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.Content;
using Android.Views;
using Android.Widget;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>
/// TV start screen: finds Animarr servers on the network (mDNS + subnet probe),
/// checks each one actually answers (<c>/api/server/info</c>, with its title
/// count), lists servers on the TV's own subnet first and puts focus on the
/// first card — so on a normal home network the user just presses OK. Typing
/// an address is a secondary action; the on-screen keyboard only opens when
/// the user asks for it (a focused text field on a TV pops the keyboard over
/// the whole screen).
/// </summary>
public sealed class TvServerPickerPage : ContentPage
{
    public TvServerPickerPage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => new TvServerPickerView(ctx));
    }
}

internal sealed class TvServerPickerView : FrameLayout
{
    private readonly IAnimarrApiClient _api;
    private readonly ServerAddressProvider _addr;
    private readonly SubnetProbeService _subnet;
    private readonly MdnsBrowserService _mdns;
    private readonly LinearLayout _cards;
    private readonly TextView _status;
    private readonly LinearLayout _manualBox;
    private readonly EditText _url;
    private readonly TextView _manualToggle;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _myPrefixes = LocalPrefixes();
    private bool _busy, _userMoved;

    public TvServerPickerView(Context ctx) : base(ctx)
    {
        var s = IPlatformApplication.Current!.Services;
        _api = s.GetRequiredService<IAnimarrApiClient>();
        _addr = s.GetRequiredService<ServerAddressProvider>();
        _subnet = s.GetRequiredService<SubnetProbeService>();
        _mdns = s.GetRequiredService<MdnsBrowserService>();
        SetBackgroundColor(TvUi.Bg);

        var col = TvUi.Column(ctx);
        col.SetClipChildren(false);
        col.SetClipToPadding(false);
        AddView(col, new LayoutParams(TvUi.Dp(ctx, 620), -2) { Gravity = GravityFlags.Center });

        col.AddView(TvUi.Text_(ctx, 22, TvUi.Text, TvUi.Semibold, 1, "Animarr"));
        col.AddView(TvUi.Text_(ctx, TvUi.TDisplay, TvUi.Text, TvUi.Semibold, 2,
            TvL.T("tv.connect_title", "Подключение к серверу", "Connect to your server")), TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 18)));
        _status = TvUi.Text_(ctx, 16, TvUi.TextDim, TvUi.Regular, 2, TvL.T("tv.searching", "Ищем серверы Animarr в сети…", "Looking for Animarr servers on your network…"));
        col.AddView(_status, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 10)));

        _cards = TvUi.Column(ctx);
        _cards.SetClipChildren(false);
        _cards.SetClipToPadding(false);
        col.AddView(_cards, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 22)));

        _manualToggle = TvUi.Button(ctx, TvL.T("tv.enter_address", "Ввести адрес вручную", "Enter the address manually"), false, ShowManual, sp: 15);
        col.AddView(_manualToggle, TvUi.Lp(-2, TvUi.Dp(ctx, 44), 0, TvUi.Dp(ctx, 18)));

        _manualBox = TvUi.Column(ctx);
        _manualBox.Visibility = ViewStates.Gone;
        _url = new EditText(ctx)
        {
            Hint = "192.168.1.50:8080",
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri,
            ImeOptions = Android.Views.InputMethods.ImeAction.Go,
        };
        _url.SetSingleLine(true);
        _url.SetTextSize(Android.Util.ComplexUnitType.Sp, 18);
        _url.SetTextColor(TvUi.Text);
        _url.SetHintTextColor(TvUi.TextFaint);
        int p = TvUi.Dp(ctx, 16);
        _url.SetPadding(p, 0, p, 0);
        var normal = TvUi.Round(ctx, TvUi.Surface, 6, TvUi.Stroke, 1);
        var focus = TvUi.Round(ctx, TvUi.Surface2, 6, AColor.White, 2);
        _url.Background = normal;
        _url.FocusChange += (_, e) => _url.Background = e.HasFocus ? focus : normal;
        _url.EditorAction += (_, e) => { e.Handled = true; _ = ConnectAsync(_url.Text); };
        var saved = Preferences.Get(TvRoot.ServerKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) _url.Text = saved;
        _manualBox.AddView(_url, TvUi.Lp(-1, TvUi.Dp(ctx, 52)));
        _manualBox.AddView(TvUi.Button(ctx, TvL.T("tv.connect", "Подключиться", "Connect"), true, () => _ = ConnectAsync(_url.Text)),
            TvUi.Lp(-2, TvUi.Dp(ctx, 44), 0, TvUi.Dp(ctx, 12)));
        col.AddView(_manualBox, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 14)));

        // Start with focus on the "enter manually" button (not on a text field,
        // which would open the keyboard); the first found server takes it over.
        _manualToggle.Post(() => _manualToggle.RequestFocus());
        _ = ScanAsync();
    }

    private void ShowManual()
    {
        _userMoved = true;
        _manualBox.Visibility = ViewStates.Visible;
        _url.RequestFocus();
        var imm = (Android.Views.InputMethods.InputMethodManager?)Context!.GetSystemService(Context.InputMethodService);
        imm?.ShowSoftInput(_url, Android.Views.InputMethods.ShowFlags.Implicit);
    }

    // ── discovery ───────────────────────────────────────────────────────────
    private async Task ScanAsync()
    {
        var a = AddAsync(Safe(_subnet.ProbeSubnetsAsync()));
        var b = AddAsync(Safe(_mdns.BrowseAsync()));
        // the last saved server is a candidate too (it may sit on another subnet)
        var saved = Preferences.Get(TvRoot.ServerKey, "");
        var c = string.IsNullOrWhiteSpace(saved) ? Task.CompletedTask
              : AddAsync(Task.FromResult(new[] { new DiscoveredServer("Animarr", Normalize(saved), "", "") }));
        await Task.WhenAll(a, b, c);
        Post(() =>
        {
            if (_seen.Count == 0 || _cards.ChildCount == 0)
                _status.Text = TvL.T("tv.none_found", "Серверы не найдены. Введите адрес сервера вручную.", "No servers found. Enter the server address manually.");
        });
    }

    private static async Task<DiscoveredServer[]> Safe(Task<DiscoveredServer[]> t)
    {
        try { return await t; } catch { return Array.Empty<DiscoveredServer>(); }
    }

    private async Task AddAsync(Task<DiscoveredServer[]> source)
    {
        var found = await source;
        await Task.WhenAll(found.Select(async s =>
        {
            var url = Normalize(s.BaseUrl);
            if (!_seen.Add(url)) return;
            // only list servers that answer right now (stale mDNS records,
            // other networks' servers) — and show what they hold
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            ServerInfoDto? info = null;
            try { info = await _api.GetServerInfoAsync(url, cts.Token); } catch { }
            if (info is null) return;
            if (!string.IsNullOrEmpty(info.ServerId) && !_seen.Add("id:" + info.ServerId)) return;   // same server, two addresses
            Post(() => AddCard(url, info));
        }));
    }

    private void AddCard(string url, ServerInfoDto info)
    {
        var ctx = Context!;
        var local = SameSubnet(url);
        var card = TvUi.Column(ctx);
        int hp = TvUi.Dp(ctx, 20), vp = TvUi.Dp(ctx, 14);
        card.SetPadding(hp, vp, hp, vp);
        card.Focusable = true; card.Clickable = true; card.FocusableInTouchMode = false;
        var title = TvUi.Text_(ctx, 19, TvUi.Text, TvUi.Medium, 1, string.IsNullOrWhiteSpace(info.Name) ? "Animarr" : info.Name);
        var meta = TvUi.Text_(ctx, 14, TvUi.TextDim, TvUi.Regular, 1,
            $"{new Uri(url).Authority}  ·  {TvL.F("tv.titles_fmt", "{0} тайтлов", "{0} titles", info.TitleCount)}");
        card.AddView(title);
        card.AddView(meta, TvUi.Lp(-1, -2, 0, TvUi.Dp(ctx, 4)));
        var normal = TvUi.Round(ctx, TvUi.Surface, 8);
        var focus = TvUi.Round(ctx, AColor.White, 8);
        card.Background = normal;
        card.FocusChange += (_, e) =>
        {
            card.Background = e.HasFocus ? focus : normal;
            title.SetTextColor(e.HasFocus ? AColor.ParseColor("#1a1a1a") : TvUi.Text);
            meta.SetTextColor(e.HasFocus ? AColor.ParseColor("#404040") : TvUi.TextDim);
            if (e.HasFocus && card.HasFocus) { /* user moved here */ }
        };
        card.Click += (_, _) => _ = ConnectAsync(url);

        // servers on the TV's own subnet first
        var lp = TvUi.Lp(-1, -2, 0, 0, 0, TvUi.Dp(ctx, 12));
        if (local) _cards.AddView(card, 0, lp); else _cards.AddView(card, lp);

        var n = _cards.ChildCount;
        _status.Text = n == 1
            ? TvL.T("tv.found_one", "Найден сервер — нажмите OK, чтобы подключиться.", "Server found — press OK to connect.")
            : TvL.F("tv.found_many", "Найдено серверов: {0}. Выберите свой.", "{0} servers found. Pick yours.", n);
        if (!_userMoved && !_manualBox.HasFocus) _cards.GetChildAt(0)?.RequestFocus();
    }

    // ── connect ─────────────────────────────────────────────────────────────
    private async Task ConnectAsync(string? raw)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(raw)) { _status.Text = TvL.T("tv.need_address", "Введите адрес сервера", "Enter the server address"); return; }
        var url = Normalize(raw);
        _busy = true;
        _status.Text = TvL.T("tv.checking", "Подключаемся…", "Connecting…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var info = await _api.GetServerInfoAsync(url, cts.Token);
            if (info is null)
            {
                _status.Text = TvL.T("tv.no_answer", "Сервер не отвечает по этому адресу", "The server does not answer at this address");
                _busy = false;
                return;
            }
            Preferences.Set(TvRoot.ServerKey, url);
            _addr.Current = new Uri(url);
            await TvAuthFlow.RouteOnOpenAsync();
        }
        catch
        {
            _status.Text = TvL.T("tv.connect_failed", "Не удалось подключиться. Проверьте адрес.", "Could not connect. Check the address.");
            _busy = false;
        }
    }

    private static string Normalize(string raw)
    {
        raw = raw.Trim();
        if (!raw.Contains("://")) raw = "http://" + raw;
        return raw.TrimEnd('/');
    }

    private bool SameSubnet(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return _myPrefixes.Any(p => host.StartsWith(p, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    /// <summary>"192.168.11." for every IPv4 /24 this device sits on.</summary>
    private static string[] LocalPrefixes()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => { var s = a.Address.ToString(); return s[..(s.LastIndexOf('.') + 1)]; })
                .Distinct().ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
#endif
