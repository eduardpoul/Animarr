using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.App;

/// <summary>
/// POC native catalog. Self-contained (own HttpClient, hard-coded LAN server)
/// so it doesn't depend on Blazor DI / cookie plumbing — judges native
/// CollectionView look + scroll + D-pad on the Mi TV. Web-matching hero +
/// category chips (functional) + 8-column true-2:3 poster grid. Card tap pushes
/// a native detail page.
/// </summary>
public partial class CatalogNativePage : ContentPage
{
    private const string ServerBase = "http://192.168.11.200:8080";
    private const int    Span       = 8;
    private const double Spacing    = 16;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly BindableProperty ArtHeightProperty =
        BindableProperty.Create(nameof(ArtHeight), typeof(double), typeof(CatalogNativePage), 300.0);
    public double ArtHeight { get => (double)GetValue(ArtHeightProperty); set => SetValue(ArtHeightProperty, value); }

    public static readonly BindableProperty HeroProperty =
        BindableProperty.Create(nameof(Hero), typeof(HeroVm), typeof(CatalogNativePage));
    public HeroVm? Hero { get => (HeroVm?)GetValue(HeroProperty); set => SetValue(HeroProperty, value); }

    public static readonly BindableProperty ChipsProperty =
        BindableProperty.Create(nameof(Chips), typeof(System.Collections.IList), typeof(CatalogNativePage));
    public System.Collections.IList? Chips { get => (System.Collections.IList?)GetValue(ChipsProperty); set => SetValue(ChipsProperty, value); }

    public Command<PosterItem> OpenCommand { get; }
    public Command<string>     ChipCommand { get; }

    private List<PosterItem> _all = new();
    private (string Name, int Count)[] _chipGroups = Array.Empty<(string, int)>();
    private int _total;

    public CatalogNativePage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        OpenCommand = new Command<PosterItem>(OpenDetail);
        ChipCommand = new Command<string>(FilterByCategory);
        ComputeArtHeight(0);
        PostersView.Loaded += OnCollectionLoaded;
        PostersView.SelectionChanged += OnPosterSelected;
        TvFocus.Attach(PostersView);
        _ = LoadAsync();
    }

    // D-pad OK on a focused card fires CollectionView selection → open detail.
    private void OnPosterSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is PosterItem p) OpenDetail(p);
        PostersView.SelectedItem = null;   // allow re-selecting the same card
    }

    // Long-tail screens reuse the existing Blazor UI in a pushed WebView host.
    private async void OnSettings(object? sender, EventArgs e)
        => await Navigation.PushAsync(new BlazorHostPage("/settings"));

    private async void OnSearch(object? sender, EventArgs e)
        => await Navigation.PushAsync(new BlazorHostPage("/search"));

    private void OnCollectionLoaded(object? sender, EventArgs e)
        => ComputeArtHeight(PostersView.Width);

    // True 2:3 art: card width = (avail - gaps) / span, height = width * 1.5.
    private void ComputeArtHeight(double viewWidth)
    {
        try
        {
            double avail = viewWidth;
            if (avail <= 0)
            {
                var info = DeviceDisplay.Current.MainDisplayInfo;
                avail = info.Density > 0 ? info.Width / info.Density : 960;
            }
            var cardW = (avail - (Span - 1) * Spacing - 16) / Span;
            if (cardW > 20) ArtHeight = Math.Round(cardW * 1.5);
        }
        catch { }
    }

    private async void OpenDetail(PosterItem? p)
    {
        if (p is null || string.IsNullOrEmpty(p.Id)) return;
        await Navigation.PushAsync(new NativeDetailPage(p.Id, p.Title, p.BackdropUrl));
    }

    private void FilterByCategory(string? cat)
    {
        cat ??= "All";
        PostersView.ItemsSource = (cat == "All")
            ? _all
            : _all.Where(p => p.Categories.Contains(cat)).ToList();
        RebuildChips(cat);
    }

    private async Task LoadAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var items = await http.GetFromJsonAsync<ApiItem[]>($"{ServerBase}/api/media?take=500", Json)
                        ?? Array.Empty<ApiItem>();

            _all = items.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                        .Select(ToPoster).ToList();

            PostersView.ItemsSource = _all;
            BuildHero(items);
            BuildChips(items);
#if ANDROID
            PreloadImages(_all);
#endif
        }
        catch { /* POC: leave empty on failure */ }
    }

    private void BuildHero(ApiItem[] items)
    {
        var h = items.Where(i => !string.IsNullOrEmpty(i.FanartPath))
                     .OrderByDescending(i => i.Rating ?? 0)
                     .FirstOrDefault();
        if (h is null) return;
        var meta = new[] { h.Year?.ToString(), h.Rating is > 0 ? $"★ {h.Rating:F1}" : null }
            .Where(s => !string.IsNullOrEmpty(s));
        Hero = new HeroVm
        {
            Backdrop = $"{ServerBase}/api/image?path={Uri.EscapeDataString(h.FanartPath!)}&w=1280",
            Eyebrow  = "FEATURED",
            Title    = (h.Title ?? "").ToUpperInvariant(),
            Meta     = string.Join("   ·   ", meta),
        };
    }

    private void BuildChips(ApiItem[] items)
    {
        _chipGroups = items
            .SelectMany(i => i.CategoryNames ?? Array.Empty<string>())
            .GroupBy(n => n)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .ToArray();
        _total = items.Length;
        RebuildChips("All");
    }

    private void RebuildChips(string active)
    {
        var chips = new List<ChipVm> { new("All", _total.ToString(), active == "All") };
        chips.AddRange(_chipGroups.Select(g => new ChipVm(g.Name, g.Count.ToString(), active == g.Name)));
        Chips = chips;
    }

#if ANDROID
    private static void PreloadImages(IEnumerable<PosterItem> posters)
    {
        try
        {
            var rm = Bumptech.Glide.Glide.With(Android.App.Application.Context);
            foreach (var p in posters) { try { rm.Load(p.ImageUrl).Preload(); } catch { } }
        }
        catch { }
    }
#endif

    private static PosterItem ToPoster(ApiItem i)
    {
        var path  = i.PosterPath ?? i.FanartPath!;
        var parts = new List<string>();
        if (i.Year is > 0)         parts.Add(i.Year!.Value.ToString());
        if (i.EpisodeCount is > 0) parts.Add($"{i.EpisodeCount} EP");
        if (i.Rating is > 0)       parts.Add($"★ {i.Rating:F1}");

        return new PosterItem
        {
            Id          = i.Id ?? "",
            Title       = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl    = $"{ServerBase}/api/image?path={Uri.EscapeDataString(path)}&w=330",
            BackdropUrl = string.IsNullOrEmpty(i.FanartPath) ? null
                        : $"{ServerBase}/api/image?path={Uri.EscapeDataString(i.FanartPath)}&w=1280",
            TypeLabel   = TypeLabel(i.MediaType),
            Meta        = string.Join("   ·   ", parts),
            Cjk         = i.CjkTitle ?? "",
            HueGlow     = HueGlow(i.Hue),
            Categories  = i.CategoryNames ?? Array.Empty<string>(),
        };
    }

    private static string TypeLabel(string? t) => t switch
    {
        "Anime"       => "ANIME",
        "Movie"       => "MOVIE",
        "Series"      => "SERIES",
        "Multserials" => "MULTI",
        _             => "MEDIA",
    };

    private static Brush HueGlow(int? hue)
    {
        var c = Color.FromHsla((hue ?? 0) / 360.0, 0.5, 0.62);
        return new RadialGradientBrush
        {
            Center = new Point(0.5, 0.0),
            Radius = 0.9,
            GradientStops =
            {
                new GradientStop(c.WithAlpha(0.18f), 0f),
                new GradientStop(Colors.Transparent, 0.65f),
            },
        };
    }

    private sealed record ApiItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath, string? CjkTitle,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount, int? Hue,
        string[]? CategoryNames);

    public sealed class PosterItem
    {
        public string   Id          { get; init; } = "";
        public string   Title       { get; init; } = "";
        public string   ImageUrl    { get; init; } = "";
        public string?  BackdropUrl { get; init; }
        public string   TypeLabel   { get; init; } = "";
        public string   Meta        { get; init; } = "";
        public string   Cjk         { get; init; } = "";
        public Brush?   HueGlow     { get; init; }
        public string[] Categories  { get; init; } = Array.Empty<string>();
    }

    public sealed class HeroVm
    {
        public string Backdrop { get; init; } = "";
        public string Eyebrow  { get; init; } = "";
        public string Title    { get; init; } = "";
        public string Meta     { get; init; } = "";
    }

    public sealed class ChipVm
    {
        public string Label    { get; }
        public string Count    { get; }
        public string Category { get; }
        public Color  Bg       { get; }
        public Color  Fg       { get; }
        public Color  CountFg  { get; }

        public ChipVm(string label, string count, bool active)
        {
            Label    = label;
            Count    = count;
            Category = label;
            Bg       = active ? Color.FromArgb("#e8772e")  : Color.FromArgb("#1a1d24");
            Fg       = active ? Colors.White               : Color.FromArgb("#c7ccd4");
            CountFg  = active ? Color.FromArgb("#ffe0c8")  : Color.FromArgb("#6b7280");
        }
    }
}
