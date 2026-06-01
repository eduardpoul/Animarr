using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.App;

/// <summary>
/// POC native catalog. Self-contained (own HttpClient, hard-coded LAN server)
/// so it doesn't depend on Blazor DI / cookie plumbing — the point is to judge
/// native CollectionView look + scroll + D-pad on the Mi TV. Card matches the
/// Blazor Poster (poster + hue glow + wash + type ribbon + CJK + title/meta);
/// art is a true 2:3 (height computed from the column width).
/// </summary>
public partial class CatalogNativePage : ContentPage
{
    private const string ServerBase = "http://192.168.11.200:8080";
    private const int    Span       = 8;
    private const double Spacing    = 16;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly BindableProperty ArtHeightProperty =
        BindableProperty.Create(nameof(ArtHeight), typeof(double), typeof(CatalogNativePage), 300.0);

    public double ArtHeight
    {
        get => (double)GetValue(ArtHeightProperty);
        set => SetValue(ArtHeightProperty, value);
    }

    public static readonly BindableProperty HeroProperty =
        BindableProperty.Create(nameof(Hero), typeof(HeroVm), typeof(CatalogNativePage));
    public HeroVm? Hero { get => (HeroVm?)GetValue(HeroProperty); set => SetValue(HeroProperty, value); }

    public static readonly BindableProperty ChipsProperty =
        BindableProperty.Create(nameof(Chips), typeof(System.Collections.IList), typeof(CatalogNativePage));
    public System.Collections.IList? Chips { get => (System.Collections.IList?)GetValue(ChipsProperty); set => SetValue(ChipsProperty, value); }

    public CatalogNativePage()
    {
        InitializeComponent();
        ComputeArtHeight(0);                 // seed from the screen so first paint is 2:3
        PostersView.Loaded += OnCollectionLoaded;
        _ = LoadAsync();
    }

    // True 2:3 art: card width = (avail - gaps) / span, height = width * 1.5.
    // Prefer the CollectionView's real width; fall back to the display metrics.
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
        catch { /* keep the default */ }
    }

    private void OnCollectionLoaded(object? sender, EventArgs e)
    {
        ComputeArtHeight(PostersView.Width);
#if ANDROID
        if (PostersView.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView rv)
        {
            rv.SetItemViewCacheSize(28);
            rv.SetClipChildren(false);       // let a focused card's scale-up overflow
            rv.SetClipToPadding(false);
            // MAUI doesn't expose per-item D-pad focus, so wire it natively: make
            // each RecyclerView item view focusable and draw the focus ring + zoom
            // + elevation on it. This is what native Android-TV apps do.
            rv.AddOnChildAttachStateChangeListener(new CardFocusListener());
        }
#endif
    }

    private async Task LoadAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var items = await http.GetFromJsonAsync<ApiItem[]>(
                            $"{ServerBase}/api/media?take=500", Json)
                        ?? Array.Empty<ApiItem>();

            var posters = items
                .Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                .Select(ToPoster)
                .ToList();

            PostersView.ItemsSource = posters;
            BuildHero(items);
            BuildChips(items);
#if ANDROID
            PreloadImages(posters);
#endif
        }
        catch { /* POC: leave the screen empty on failure */ }
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
        var groups = items
            .SelectMany(i => i.CategoryNames ?? Array.Empty<string>())
            .GroupBy(n => n)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();

        var chips = new List<ChipVm> { new("All", items.Length.ToString(), active: true) };
        chips.AddRange(groups.Select(g => new ChipVm(g.Name, g.Count.ToString(), active: false)));
        Chips = chips;
    }

#if ANDROID
    // MAUI Android loads images via Glide; preloading the URLs warms the shared
    // Glide cache so each card is a cache hit when it scrolls in.
    private static void PreloadImages(IEnumerable<PosterItem> posters)
    {
        try
        {
            var rm = Bumptech.Glide.Glide.With(Android.App.Application.Context);
            foreach (var p in posters)
            {
                try { rm.Load(p.ImageUrl).Preload(); } catch { }
            }
        }
        catch { /* best-effort */ }
    }

    // Native D-pad focus + highlight for the RecyclerView items.
    private sealed class CardFocusListener : Java.Lang.Object,
        AndroidX.RecyclerView.Widget.RecyclerView.IOnChildAttachStateChangeListener
    {
        public void OnChildViewAttachedToWindow(Android.Views.View view)
        {
            view.Focusable = true;
            view.FocusableInTouchMode = false;
            view.FocusChange -= OnFocus;
            view.FocusChange += OnFocus;
        }

        public void OnChildViewDetachedFromWindow(Android.Views.View view)
            => view.FocusChange -= OnFocus;

        private static void OnFocus(object? sender, Android.Views.View.FocusChangeEventArgs e)
        {
            if (sender is not Android.Views.View v) return;
            var d = v.Resources?.DisplayMetrics?.Density ?? 2f;

            v.Animate()?.ScaleX(e.HasFocus ? 1.10f : 1f)?.ScaleY(e.HasFocus ? 1.10f : 1f)?
                .SetDuration(120)?.Start();

            if (e.HasFocus)
            {
                var ring = new Android.Graphics.Drawables.GradientDrawable();
                ring.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                ring.SetCornerRadius(12 * d);
                ring.SetStroke((int)(3 * d), Android.Graphics.Color.ParseColor("#84c0d2"));
                v.Foreground = ring;
                v.Elevation = 12 * d;
                v.BringToFront();
            }
            else
            {
                v.Foreground = null;
                v.Elevation = 0;
            }
        }
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
            Title     = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl  = $"{ServerBase}/api/image?path={Uri.EscapeDataString(path)}&w=330",
            TypeLabel = TypeLabel(i.MediaType),
            Meta      = string.Join("   ·   ", parts),
            Cjk       = i.CjkTitle ?? "",
            HueGlow   = HueGlow(i.Hue),
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
        string? Title, string? PosterPath, string? FanartPath, string? CjkTitle,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount, int? Hue,
        string[]? CategoryNames);

    public sealed class PosterItem
    {
        public string Title     { get; init; } = "";
        public string ImageUrl  { get; init; } = "";
        public string TypeLabel { get; init; } = "";
        public string Meta      { get; init; } = "";
        public string Cjk       { get; init; } = "";
        public Brush? HueGlow   { get; init; }
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
        public string Label   { get; }
        public string Count   { get; }
        public Color  Bg      { get; }
        public Color  Fg      { get; }
        public Color  CountFg { get; }

        public ChipVm(string label, string count, bool active)
        {
            Label   = label;
            Count   = count;
            Bg      = active ? Color.FromArgb("#e8772e")  : Color.FromArgb("#1a1d24");
            Fg      = active ? Colors.White               : Color.FromArgb("#c7ccd4");
            CountFg = active ? Color.FromArgb("#ffe0c8")  : Color.FromArgb("#6b7280");
        }
    }
}
