using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace Animarr.App;

/// <summary>
/// Splash + startup router for the TV shell. On first appear it decides which
/// onboarding stage to show:
///   • no saved server         → <see cref="ServerPickerPage"/>
///   • saved                    → <see cref="TvAuthFlow.RouteOnOpenAsync"/>: the
///     device's default profile, the "Who's watching" picker, the "Set a PIN"
///     step, the catalog or pairing
/// A saved-but-unreachable server falls back to the picker so the user can
/// re-point the TV without reinstalling.
/// </summary>
public partial class TvBootstrapPage : ContentPage
{
    public TvBootstrapPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RouteAsync();
    }

    private async Task RouteAsync()
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        var addr = services.GetRequiredService<ServerAddressProvider>();
        var api  = services.GetRequiredService<IAnimarrApiClient>();

        var saved = Preferences.Get(TvRoot.ServerKey, "");
        if (string.IsNullOrWhiteSpace(saved))
        {
            TvRoot.Go(new ServerPickerPage());
            return;
        }

        addr.Current = new Uri(saved);
        StatusLabel.Text = "Подключение…";
        try
        {
            // Probe first: an unreachable saved server falls back to the picker.
            await api.GetAuthStatusAsync();
            // Device gates: default profile → straight in; ≥2 profiles → "Who's
            // watching"; missing PIN → "Set a PIN"; else catalog / pairing.
            await TvAuthFlow.RouteOnOpenAsync();
        }
        catch
        {
            // Saved server moved / offline — let the user re-pick.
            TvRoot.Go(new ServerPickerPage());
        }
    }
}
