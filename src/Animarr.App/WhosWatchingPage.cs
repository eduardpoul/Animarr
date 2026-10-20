using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Microsoft.Maui.Controls.Shapes;

namespace Animarr.App;

/// <summary>
/// Native TV "Who's watching?" picker — the same rules as the Blazor /switch
/// page, on the same server API (GET /api/auth/device):
///
///   • Startup — shown at launch when this TV knows ≥2 profiles and has no
///     "Sign in by default" profile, or when the session expired but PIN
///     profiles remain. Every profile asks its PIN (server-verified).
///   • Switch — opened from the profile menu; always shows the list.
///
/// A profile without a PIN on this TV goes through pairing (the TV's password
/// sign-in) and then the "Set a PIN" step. The "Sign in by default" switch at
/// the bottom applies to the profile picked: on → it opens this TV straight
/// away next time (no picker, no PIN); off → the default is cleared.
/// </summary>
public sealed class WhosWatchingPage : ContentPage
{
    public enum PickerMode { Startup, Switch }

    private static readonly Color Bg     = Color.FromArgb("#0a0c10");
    private static readonly Color Card   = Color.FromArgb("#151a21");
    private static readonly Color Accent = Color.FromArgb("#e8772e");
    private static readonly Color Dim    = Color.FromArgb("#7b8290");

    private readonly PickerMode _mode;
    private readonly FlexLayout _grid = new()
    {
        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
        Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
        JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Center,
    };
    private readonly Label _toggleValue = new() { FontFamily = "Geist", FontSize = 15, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly Label _toggleHint  = new() { TextColor = Dim, FontFamily = "Geist", FontSize = 13, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _status      = new() { TextColor = Color.FromArgb("#e0908f"), FontFamily = "Geist", FontSize = 13, HorizontalTextAlignment = TextAlignment.Center };
    private DeviceStateDto? _state;
    private bool _defaultOn;
    private bool _loaded;
    private Border? _firstCard;

    public WhosWatchingPage(PickerMode mode)
    {
        _mode = mode;
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Bg;
        Content = BuildLayout();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
        {
            // Back from the PIN pad (cancel) — keep the list, refocus it.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), FocusFirst);
            return;
        }
        _loaded = true;
        _ = LoadAsync();
    }

    private View BuildLayout()
    {
        var title = new Label
        {
            Text = TvL.T("switch.title", "Кто смотрит?", "Who's watching?"),
            TextColor = Colors.White, FontFamily = "ArchivoBlack", FontAttributes = FontAttributes.Bold,
            FontSize = 34, HorizontalTextAlignment = TextAlignment.Center,
        };

        var toggle = new Border
        {
            StrokeThickness = 1, Stroke = Color.FromArgb("#26FFFFFF"),
            BackgroundColor = Card, StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(20, 12), HorizontalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = TvL.T("switch.default_label", "Входить по умолчанию", "Sign in by default"),
                        TextColor = Colors.White, FontFamily = "Geist", FontSize = 15,
                        VerticalOptions = LayoutOptions.Center,
                    },
                    _toggleValue,
                },
            },
        };
        toggle.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = new Command(async () => await ToggleDefaultAsync()) });

        var actions = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center };
        if (_mode == PickerMode.Switch)
            actions.Children.Add(Link(TvL.T("login.back", "Назад", "Back"), async () => await Navigation.PopAsync()));
        actions.Children.Add(Link(TvL.T("switch.pair_phone", "Войти через телефон", "Sign in with a phone"),
            async () => await Navigation.PushAsync(new PairingPage())));

        RenderToggle();

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(56, 60, 56, 40),
                Spacing = 28,
                MaximumWidthRequest = 1100,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    title,
                    _grid,
                    _status,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#1fffffff"), Margin = new Thickness(120, 4) },
                    toggle,
                    _toggleHint,
                    actions,
                },
            },
        };
    }

    private static Border Link(string text, Action act)
    {
        var b = new Border
        {
            StrokeThickness = 0, BackgroundColor = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(18, 10),
            Content = new Label { Text = text, TextColor = Color.FromArgb("#cfd3da"), FontFamily = "Geist", FontSize = 15 },
        };
        b.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = new Command(act) });
        return b;
    }

    private async Task LoadAsync()
    {
        _state = await TvAuthFlow.TryGetDeviceAsync();
        if (_state is null)
        {
            // Older server / offline: nothing to pick from.
            TvRoot.Go(new PairingPage());
            return;
        }
        _defaultOn = _state.DefaultUserId is not null;
        RenderToggle();

        _grid.Children.Clear();
        _firstCard = null;
        foreach (var p in _state.Profiles)
        {
            var card = BuildCard(p);
            _firstCard ??= card;
            _grid.Children.Add(card);
        }
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), FocusFirst);
    }

    private void FocusFirst()
    {
#if ANDROID
        try { (_firstCard?.Handler?.PlatformView as global::Android.Views.View)?.RequestFocus(); }
        catch { }
#endif
    }

    private Border BuildCard(DeviceProfileDto p)
    {
        var signedIn = _state?.CurrentUserId is not null;
        var role = !p.HasPin ? TvL.T("switch.password_needed", "Вход по паролю", "Sign in with password")
                 : p.IsDefault ? TvL.T("switch.default_badge", "По умолчанию", "Default")
                 : p.IsCurrent && signedIn ? TvL.T("switch.you", "Вы", "You")
                 : p.RoleName;
        var initial = string.IsNullOrWhiteSpace(p.Name) ? "?" : p.Name.Trim()[..1].ToUpperInvariant();

        var disc = new Border
        {
            WidthRequest = 96, HeightRequest = 96,
            StrokeThickness = p.IsDefault ? 3 : 0,
            Stroke = p.IsDefault ? Accent : null,
            BackgroundColor = Color.FromHsla(p.AvatarHue / 360.0, 0.5, 0.5),
            StrokeShape = new RoundRectangle { CornerRadius = 48 },
            HorizontalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = initial, TextColor = Colors.White, FontFamily = "ArchivoBlack",
                FontAttributes = FontAttributes.Bold, FontSize = 36,
                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            },
        };
        var card = new Border
        {
            StrokeThickness = 0, BackgroundColor = Colors.Transparent,
            Padding = new Thickness(18, 14), Margin = new Thickness(8),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            WidthRequest = 160,
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    disc,
                    new Label
                    {
                        Text = p.Name, TextColor = Colors.White, FontFamily = "Geist", FontSize = 16,
                        FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.TailTruncation,
                    },
                    new Label
                    {
                        Text = (p.HasPin ? "🔒 " : "🔑 ") + role,
                        TextColor = Dim, FontFamily = "Geist", FontSize = 12,
                        HorizontalTextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 12, Command = new Command(async () => await PickAsync(p)) });
        return card;
    }

    /// <summary>The default to apply to whoever ends up signed in: on → them;
    /// off → clear an existing default; nothing to change → null.</summary>
    private bool? DefaultChoice => _defaultOn ? true : _state?.DefaultUserId is not null ? false : null;

    private async Task PickAsync(DeviceProfileDto p)
    {
        if (_state is null) return;
        _status.Text = "";
        var signedInAsThem = _state.CurrentUserId == p.Id;

        // Profile menu → yourself: already in; just apply the default switch.
        if (_mode == PickerMode.Switch && signedInAsThem && p.HasPin)
        {
            await TvAuthFlow.ApplyDefaultAsync(DefaultChoice);
            TvRoot.Go(new CatalogNativePage());
            return;
        }
        if (p.HasPin)
        {
            await Navigation.PushAsync(TvPinPage.ForVerify(p, DefaultChoice, _state.PinMinLength, _state.PinMaxLength));
            return;
        }
        // No PIN on this TV yet: the live session's own profile sets one now;
        // anyone else signs in by pairing first (the TV's password sign-in).
        if (signedInAsThem)
            TvRoot.Go(TvPinPage.ForSetup(_defaultOn));
        else
            await Navigation.PushAsync(new PairingPage());
    }

    private async Task ToggleDefaultAsync()
    {
        _defaultOn = !_defaultOn;
        RenderToggle();
        // Turning the default OFF from the profile menu applies right away (it
        // only brings the picker back); ON waits for the pick.
        if (!_defaultOn && _mode == PickerMode.Switch
            && _state is { CurrentUserId: not null, DefaultUserId: not null })
        {
            try
            {
                var fresh = await TvAuthFlow.Api.SetDeviceDefaultAsync(new SetDeviceDefaultRequest(false));
                if (fresh is not null) _state = fresh;
            }
            catch
            {
                _status.Text = TvL.T("switch.default_err", "Не удалось изменить профиль по умолчанию.",
                    "Couldn't change the default profile.");
            }
            RenderToggle();
        }
    }

    private void RenderToggle()
    {
        _toggleValue.Text = _defaultOn ? TvL.T("common.on", "Вкл.", "On") : TvL.T("common.off", "Выкл.", "Off");
        _toggleValue.TextColor = _defaultOn ? Accent : Dim;

        var current = _state?.DefaultUserId is Guid id ? _state.Profiles.FirstOrDefault(p => p.Id == id) : null;
        _toggleHint.Text = !_defaultOn
            ? TvL.T("switch.default_hint_off", "Animarr спрашивает, кто смотрит, при каждом запуске.",
                "Animarr asks who's watching each time it opens.")
            : current is not null
                ? TvL.F("switch.default_hint_current_fmt", "На этом устройстве сразу открывается {0}.",
                    "{0} opens automatically on this device.", current.Name)
                : TvL.T("switch.default_hint_on", "Выбранный профиль будет открываться сразу — без выбора и без PIN.",
                    "The profile you pick will open automatically — no picker, no PIN.");
    }
}
