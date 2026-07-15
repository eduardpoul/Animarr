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
        // D-pad OK via the behavior (set here) — a gesture Command bound through
        // {x:Reference Root} resolves to null on TV.
        ChangeServerFocus.Command = ChangeServerCommand;
        LogoutFocus.Command       = LogoutCommand;
        BackFocus.Command         = new Command(async () => await Navigation.PopAsync());
        LangSelectFocus.Command   = new Command(async () => await PickLanguageAsync());
        ScaleSelectFocus.Command  = new Command(async () => await PickScaleAsync());

        ApplyStrings();
        ApplyScaleValue();
        _ = LoadAsync();
    }

    /// <summary>Localized labels (lang pack already loaded by the home page).</summary>
    private void ApplyStrings()
    {
        EyebrowLabel.Text      = TvL.T("profile.title", "Профиль", "Profile").ToUpperInvariant();
        RosterTitle.Text       = TvL.T("profile.whos_watching", "Кто смотрит", "Who's watching");
        LangTitle.Text         = TvL.T("settings.language_label", "Язык интерфейса", "Interface language");
        ScaleTitle.Text        = TvL.T("profile.ui_scale", "Масштаб интерфейса", "Interface scale");
        ServerTitle.Text       = TvL.T("profile.server", "Сервер", "Server");
        ChangeServerLabel.Text = TvL.T("profile.change_server", "Сменить сервер", "Change server");
        LogoutLabel.Text       = TvL.T("profile.logout", "Выйти", "Log out");
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
            // Who is signed in — the roster itself doesn't say, so the active
            // profile card can get its highlight ring.
            Guid? meId = null;
            try { meId = (await _api.GetMeAsync())?.User?.Id; } catch { }

            var roster = await _api.GetRosterAsync();
            RosterHost.Children.Clear();
            foreach (var u in roster)
                RosterHost.Children.Add(BuildProfileCard(u, u.Id == meId));
        }
        catch { }
    }

    private View BuildProfileCard(RosterUserDto u, bool current)
    {
        var initial = string.IsNullOrWhiteSpace(u.Name) ? "?" : u.Name.Trim()[..1].ToUpperInvariant();
        var disc = new Border
        {
            WidthRequest = 84, HeightRequest = 84,
            // Active profile: accent ring around the disc (web topbar chip style).
            StrokeThickness = current ? 3 : 0,
            Stroke = current ? Color.FromArgb("#e8772e") : null,
            BackgroundColor = Color.FromHsla(u.AvatarHue / 360.0, 0.5, 0.5),
            StrokeShape = new RoundRectangle { CornerRadius = 42 },
            HorizontalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = initial, TextColor = Colors.White, FontFamily = "ArchivoBlack", FontAttributes = FontAttributes.Bold,
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
                        Text = u.Name,
                        TextColor = current ? Color.FromArgb("#e8772e") : Color.FromArgb("#cfd3da"),
                        FontFamily = "Geist", FontSize = 14,
                        FontAttributes = current ? FontAttributes.Bold : FontAttributes.None,
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

    // ── Web-style selects: the closed box shows the current value; OK opens a
    // native option sheet (D-pad friendly), the pick applies immediately. ────
    private void BuildLanguages()
    {
        var name = Languages.FirstOrDefault(l =>
            string.Equals(l.Code, _currentLang, StringComparison.OrdinalIgnoreCase)).Name;
        LangValue.Text = name ?? _currentLang;
    }

    private async Task PickLanguageAsync()
    {
        var names  = Languages.Select(l => l.Name).ToArray();
        var choice = await DisplayActionSheet(
            TvL.T("settings.language_label", "Язык интерфейса", "Interface language"),
            TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, names);
        var picked = Languages.FirstOrDefault(l => l.Name == choice);
        if (picked.Code is null) return;
        await SetLanguageAsync(picked.Code);
    }

    private async Task PickScaleAsync()
    {
        var scales = Scales;
        var labels = scales.Select(s => s.Label).ToArray();
        var choice = await DisplayActionSheet(
            TvL.T("profile.ui_scale", "Масштаб интерфейса", "Interface scale"),
            TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, labels);
        var picked = scales.FirstOrDefault(s => s.Label == choice);
        if (picked.Label is null) return;
        ScaleValue.Text = picked.Label;
        SetScale(picked.Scale);
    }

    // ── Interface scale (TV text zoom) ──────────────────────────────────────
    // Persisted in plain Android SharedPreferences so MainActivity can read it
    // in AttachBaseContext (before MAUI Essentials is up) and multiply the
    // activity's fontScale. Applying requires an activity recreate.
    private static (string Label, float Scale)[] Scales =>
    new[]
    {
        (TvL.T("profile.scale_small",  "Маленький", "Small"),  0.85f),
        (TvL.T("profile.scale_normal", "Обычный",   "Normal"), 1f),
        (TvL.T("profile.scale_large",  "Большой",   "Large"),  1.2f),
    };

    /// <summary>Show the persisted scale in the closed select box.</summary>
    private void ApplyScaleValue()
    {
#if ANDROID
        float current = 1f;
        try
        {
            var sp = global::Android.App.Application.Context.GetSharedPreferences(
                MainActivity.TvPrefsName, global::Android.Content.FileCreationMode.Private);
            current = sp?.GetFloat(MainActivity.UiScalePref, 1f) ?? 1f;
        }
        catch { }
        var match = Scales.FirstOrDefault(s => Math.Abs(current - s.Scale) < 0.01f);
        ScaleValue.Text = match.Label ?? "Обычный";
#endif
    }

    private void SetScale(float scale)
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var sp = ctx.GetSharedPreferences(
                MainActivity.TvPrefsName, global::Android.Content.FileCreationMode.Private);
            sp?.Edit()?.PutFloat(MainActivity.UiScalePref, scale)?.Commit();
            StatusLabel.Text = "Применяем масштаб…";

            // The density must be picked up by a FRESH process: MAUI caches its
            // dp→px density once per process, so a soft Activity.Recreate()
            // rescales text only (native sp) while cards/buttons keep the old
            // pixel sizes. Start-then-kill: queue a ClearTask launch of ourselves
            // while still foreground (allowed), then kill the process — the
            // system recreates it to show the requested activity. An
            // AlarmManager-after-exit relaunch is blocked as a background start.
            var intent = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName ?? "");
            if (intent is not null)
            {
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask |
                                global::Android.Content.ActivityFlags.ClearTask);
                ctx.StartActivity(intent);
            }
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }
        catch { StatusLabel.Text = "Не удалось применить масштаб"; }
#endif
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
            // Pull the new pack, then rebuild the whole shell so every page picks
            // up the language (pages bake their strings at construction time).
            await TvL.LoadAsync(_http, code);
            TvRoot.Go(new CatalogNativePage());
        }
        catch
        {
            StatusLabel.Text = TvL.T("profile.language_error",
                "Не удалось сменить язык", "Couldn't change the language");
        }
    }

    private async Task LogoutAsync()
    {
        try { await _http.PostAsync(ApiRoutes.AuthLogout, null); }
        catch { }
        TvRoot.Go(new PairingPage());
    }
}
