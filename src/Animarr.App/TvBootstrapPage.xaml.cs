using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace Animarr.App;

/// <summary>
/// Splash + startup router for the TV shell. On first appear it decides which
/// onboarding stage to show:
///   • no saved server         → <see cref="ServerPickerPage"/>
///   • saved but no session     → <see cref="PairingPage"/>
///   • already authenticated    → <see cref="CatalogNativePage"/>
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
            var status = await api.GetAuthStatusAsync();
            TvRoot.Go(status.Authenticated ? new CatalogNativePage() : new PairingPage());
        }
        catch
        {
            // Saved server moved / offline — let the user re-pick.
            TvRoot.Go(new ServerPickerPage());
        }
    }
}
