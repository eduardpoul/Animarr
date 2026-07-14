using System.Windows.Input;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace Animarr.App;

/// <summary>
/// TV profile menu (top-right on the catalog): switch profile ("who's
/// watching"), pick interface language, change server, and log out. Deliberately
/// omits the deep settings — those stay in the phone/browser app. Profile switch
/// and language write straight through IAnimarrApiClient; server change and
/// logout return to the earlier onboarding stages.
/// </summary>
public partial class ProfilePanelPage : ContentPage
{
    private static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("ru", "Русский"), ("uk", "Українська"),
        ("de", "Deutsch"), ("es", "Español"),
    };

    private readonly IAnimarrApiClient _api;
    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private string _currentLang = "en";

    public ICommand ChangeServerCommand { get; }
    public ICommand LogoutCommand { get; }

    public ProfilePanelPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _api  = services.GetRequiredService<IAnimarrApiClient>();
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        ChangeServerCommand = new Command(() => TvRoot.Go(new ServerPickerPage()));
        LogoutCommand       = new Command(async () => await LogoutAsync());

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var prefs = await _api.GetMyPreferencesAsync();
            if (!string.IsNullOrWhiteSpace(prefs.Language)) _currentLang = prefs.Language;
        }
        catch { }
        BuildLanguages();

        try
        {
            var roster = await _api.GetRosterAsync();
            RosterHost.Children.Clear();
            foreach (var u in roster) RosterHost.Children.Add(BuildProfileCard(u));
        }
        catch { }
    }

    private View BuildProfileCard(RosterUserDto u)
    {
        var initial = string.IsNullOrWhiteSpace(u.Name) ? "?" : u.Name.Trim()[..1].ToUpperInvariant();
        var disc = new Border
        {
            WidthRequest = 84, HeightRequest = 84, StrokeThickness = 0,
            BackgroundColor = Color.FromHsla(u.AvatarHue / 360.0, 0.5, 0.5),
            StrokeShape = new RoundRectangle { CornerRadius = 42 },
            HorizontalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = initial, TextColor = Colors.White, FontFamily = "ArchivoBlack",
                FontSize = 32, HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };
        var card = new Border
        {
            StrokeThickness = 0, BackgroundColor = Colors.Transparent,
            Padding = new Thickness(16, 12),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    disc,
                    new Label
                    {
                        Text = u.Name, TextColor = Color.FromArgb("#cfd3da"),
                        FontFamily = "Geist", FontSize = 14,
                        HorizontalOptions = LayoutOptions.Center,
                        HorizontalTextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 12 });
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await SwitchAsync(u)),
        });
        return card;
    }

    private void BuildLanguages()
    {
        LanguageHost.Children.Clear();
        foreach (var (code, label) in Languages)
        {
            var active = string.Equals(code, _currentLang, StringComparison.OrdinalIgnoreCase);
            var chip = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = active ? Color.FromArgb("#e8772e") : Color.FromArgb("#151a21"),
                Padding = new Thickness(20, 11),
                Margin = new Thickness(0, 0, 10, 10),
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                Content = new Label
                {
                    Text = label,
                    TextColor = active ? Colors.White : Color.FromArgb("#c7ccd4"),
                    FontFamily = "Geist", FontSize = 14,
                },
            };
            chip.Behaviors.Add(new TvFocusBehavior { Radius = 20 });
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await SetLanguageAsync(code)),
            });
            LanguageHost.Children.Add(chip);
        }
    }

    private async Task SwitchAsync(RosterUserDto u)
    {
        try
        {
            var res = await _api.SwitchUserAsync(new SwitchUserRequest(u.Id, null));
            if (res is not null) TvRoot.Go(new CatalogNativePage());
            else StatusLabel.Text = "Не удалось сменить профиль";
        }
        catch { StatusLabel.Text = "Не удалось сменить профиль"; }
    }

    private async Task SetLanguageAsync(string code)
    {
        try
        {
            await _api.UpdateMyPreferencesAsync(new UpdatePreferencesRequest(
                AccentHue: null, BackdropEnabled: null, BackdropBlurPx: null,
                BackdropBrightness: null, BackdropIntervalSec: null, TvMode: null,
                AudioPreferredLanguage: null, SubtitlePreferredLanguage: null,
                SubtitleSize: null, DefaultVolume: null, AudioPassthrough: null,
                NormalizeVolume: null, Language: code));
            _currentLang = code;
            BuildLanguages();
            StatusLabel.Text = "Язык сохранён";
        }
        catch { StatusLabel.Text = "Не удалось сменить язык"; }
    }

    private async Task LogoutAsync()
    {
        try { await _http.PostAsync(ApiRoutes.AuthLogout, null); }
        catch { }
        TvRoot.Go(new PairingPage());
    }
}
