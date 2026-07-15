using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Input;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Native library search. Debounced text query hits the server-side filter
/// (<c>GET /api/media?search=</c>) and shows matches as a focusable poster grid;
/// OK opens the detail page. Uses the shared authenticated HttpClient.
/// </summary>
public partial class SearchPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');
    private CancellationTokenSource? _debounce;

    public SearchPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        // D-pad OK via the behavior (programmatic — the reliable path).
        BackFocus.Command = new Command(async () => await Navigation.PopAsync());
        SearchTitle.Text = TvL.T("search.title", "Поиск", "Search").ToUpperInvariant();
        QueryEntry.Placeholder = TvL.T("search.placeholder", "Название сериала или фильма", "Series or movie title");
        // A focused Entry does NOT summon the IME on leanback devices (non-touch
        // → the system waits for a tap that never comes). Force it.
        QueryEntry.Focused += (_, _) => ShowKeyboard();
        // The dark Border is the input's visual — drop the EditText's own
        // colored underline strip.
        QueryEntry.HandlerChanged += (_, _) =>
        {
#if ANDROID
            if (QueryEntry.Handler?.PlatformView is global::Android.Widget.EditText et)
                et.BackgroundTintList =
                    global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);
#endif
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Give the handler a beat to attach, then autofocus + raise the keyboard.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), () =>
        {
            QueryEntry.Focus();
            ShowKeyboard();
        });
    }

    private void ShowKeyboard()
    {
#if ANDROID
        try
        {
            if (QueryEntry.Handler?.PlatformView is not global::Android.Views.View v) return;
            v.RequestFocus();
            var imm = (global::Android.Views.InputMethods.InputMethodManager?)
                v.Context?.GetSystemService(global::Android.Content.Context.InputMethodService);
            imm?.ShowSoftInput(v, global::Android.Views.InputMethods.ShowFlags.Forced);
        }
        catch { }
#endif
    }

    private void OnQueryChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        _ = DebouncedSearchAsync(e.NewTextValue?.Trim() ?? "", _debounce.Token);
    }

    private async Task DebouncedSearchAsync(string q, CancellationToken ct)
    {
        try { await Task.Delay(350, ct); } catch { return; }
        if (ct.IsCancellationRequested) return;

        if (q.Length < 2)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ResultsHost.Children.Clear();
                StatusLabel.Text = "";
            });
            return;
        }

        try
        {
            var items = await _http.GetFromJsonAsync<ApiItem[]>(
                $"/api/media?search={Uri.EscapeDataString(q)}&take=120", Json, ct)
                ?? Array.Empty<ApiItem>();
            if (ct.IsCancellationRequested) return;

            var posters = items.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                               .Select(ToPoster).ToList();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ResultsHost.Children.Clear();
                foreach (var p in posters) ResultsHost.Children.Add(TvCards.BuildPosterCard(p));
                StatusLabel.Text = posters.Count == 0 ? TvL.T("search.empty", "Ничего не найдено", "Nothing found") : TvL.F("search.results_fmt", "{0} результатов", "{0} results", posters.Count);
            });
        }
        catch (OperationCanceledException) { }
        catch { MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = TvL.T("search.error", "Ошибка поиска", "Search error")); }
    }

    private CatalogNativePage.PosterItem ToPoster(ApiItem i)
    {
        var path  = i.PosterPath ?? i.FanartPath!;
        var parts = new List<string>();
        if (i.Year is > 0)         parts.Add(i.Year!.Value.ToString());
        if (i.EpisodeCount is > 0) parts.Add($"{i.EpisodeCount} EP");
        if (i.Rating is > 0)       parts.Add($"★ {i.Rating:F1}");

        var p = new CatalogNativePage.PosterItem
        {
            Id          = i.Id ?? "",
            Title       = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl    = $"{ImageBase}/api/image?path={Uri.EscapeDataString(path)}&w=330",
            BackdropUrl = string.IsNullOrEmpty(i.FanartPath) ? null
                        : $"{ImageBase}/api/image?path={Uri.EscapeDataString(i.FanartPath)}&w=1280",
            TypeLabel   = i.MediaType switch
            {
                "Anime" => "ANIME", "Movie" => "MOVIE", "Series" => "SERIES",
                "Multserials" => "MULTI", _ => "",
            },
            Meta        = string.Join(" · ", parts),
        };
        p.Open = new Command(async () =>
            await Navigation.PushAsync(new NativeDetailPage(p.Id, p.Title, p.BackdropUrl)));
        return p;
    }

    private sealed record ApiItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath,
        int? Year, double? Rating, string? MediaType, int? EpisodeCount);
}
