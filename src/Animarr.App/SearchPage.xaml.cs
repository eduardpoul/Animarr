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

    public static readonly BindableProperty ResultsProperty =
        BindableProperty.Create(nameof(Results), typeof(System.Collections.IList), typeof(SearchPage));
    public System.Collections.IList? Results { get => (System.Collections.IList?)GetValue(ResultsProperty); set => SetValue(ResultsProperty, value); }

    public ICommand OpenCommand { get; }

    public SearchPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        OpenCommand = new Command<Poster>(OpenDetail);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        QueryEntry.Focus();
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
            Results = null;
            StatusLabel.Text = "";
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
            Results = posters;
            StatusLabel.Text = posters.Count == 0 ? "Ничего не найдено" : $"{posters.Count} результатов";
        }
        catch (OperationCanceledException) { }
        catch { StatusLabel.Text = "Ошибка поиска"; }
    }

    private async void OpenDetail(Poster? p)
    {
        if (p is null || string.IsNullOrEmpty(p.Id)) return;
        await Navigation.PushAsync(new NativeDetailPage(p.Id, p.Title, p.BackdropUrl));
    }

    private Poster ToPoster(ApiItem i)
    {
        var path  = i.PosterPath ?? i.FanartPath!;
        var parts = new List<string>();
        if (i.Year is > 0)   parts.Add(i.Year!.Value.ToString());
        if (i.Rating is > 0) parts.Add($"★ {i.Rating:F1}");

        return new Poster
        {
            Id          = i.Id ?? "",
            Title       = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl    = $"{ImageBase}/api/image?path={Uri.EscapeDataString(path)}&w=330",
            BackdropUrl = string.IsNullOrEmpty(i.FanartPath) ? null
                        : $"{ImageBase}/api/image?path={Uri.EscapeDataString(i.FanartPath)}&w=1280",
            Meta        = string.Join("   ·   ", parts),
        };
    }

    private sealed record ApiItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath,
        int? Year, double? Rating);

    public sealed class Poster
    {
        public string  Id          { get; init; } = "";
        public string  Title       { get; init; } = "";
        public string  ImageUrl    { get; init; } = "";
        public string? BackdropUrl { get; init; }
        public string  Meta        { get; init; } = "";
    }
}
