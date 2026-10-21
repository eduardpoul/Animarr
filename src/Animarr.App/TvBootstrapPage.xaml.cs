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
#if ANDROID
        Bench.BenchLog.Mark("bootstrap-appearing");
#endif
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
            TvRoot.Go(TvRoot.ServerPicker());
            return;
        }

        addr.Current = new Uri(saved);
        StatusLabel.Text = "Подключение…";
        try
        {
            // Probe first: an unreachable saved server falls back to the picker.
            using (var probe = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                await api.GetAuthStatusAsync(probe.Token);
#if ANDROID
            Bench.BenchLog.Mark("bootstrap-authstatus");
            switch (Bench.BenchLog.Base)
            {
                case "rv" or "rvflat":  await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new Bench.NativeLibraryBenchPage()); return;
                case "lib": await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new LibraryAllPage()); return;
                case { } m when m.StartsWith("detail:"): await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new NativeDetailPage(m[7..], "BENCH", null)); return;
                case "nlib": await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new Tv.TvLibraryPage()); return;
                case "ndetail": await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new Tv.TvDetailPage(Bench.BenchLog.Mode!.Split(':')[1], "BENCH", null)); return;
                case "oldhome": await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new CatalogNativePage()); return;
                case "play": TvRoot.Go(new PlayerPage(Guid.Parse("00000000-0000-0000-0000-000000002716"), 1, 3, "/lib/x/e3.mkv", "BENCH", 0)); return;
                case "detail": await TvL.LoadLastAsync(TvAuthFlow.Http); TvRoot.Go(new NativeDetailPage(Bench.BenchLog.Mode!.Split(':')[1], "BENCH", null)); return;
            }
#endif
            // Device gates: default profile → straight in; ≥2 profiles → "Who's
            // watching"; missing PIN → "Set a PIN"; else catalog / pairing.
            await TvAuthFlow.RouteOnOpenAsync();
        }
        catch
        {
            // Saved server moved / offline — let the user re-pick.
            TvRoot.Go(TvRoot.ServerPicker());
        }
    }
}
