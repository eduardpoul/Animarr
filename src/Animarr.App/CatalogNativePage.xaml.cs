using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Input;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Native TV catalog: a top bar (search + profile) over a vertical stack of
/// horizontal rails — Continue watching (with a resume bar), Favorites, then a
/// rail per top category. Each poster is a native D-pad focus target
/// (TvFocusBehavior) whose OK opens the detail page. Talks to the server through
/// the shared, cookie-carrying HttpClient (see MauiProgram) so every call is
/// authenticated and follows the picked server.
/// </summary>
public partial class CatalogNativePage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    // Bound to the vertical rail list.
    public static readonly BindableProperty RailsProperty =
        BindableProperty.Create(nameof(Rails), typeof(System.Collections.IList), typeof(CatalogNativePage));
    public System.Collections.IList? Rails { get => (System.Collections.IList?)GetValue(RailsProperty); set => SetValue(RailsProperty, value); }

    public static readonly BindableProperty HeroProperty =
        BindableProperty.Create(nameof(Hero), typeof(HeroVm), typeof(CatalogNativePage));
    public HeroVm? Hero { get => (HeroVm?)GetValue(HeroProperty); set => SetValue(HeroProperty, value); }

    public ICommand OpenCommand    { get; }
    public ICommand SearchCommand  { get; }
    public ICommand ProfileCommand { get; }
    public ICommand HeroPlayCommand { get; }

    public CatalogNativePage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();

        OpenCommand     = new Command<PosterItem>(OpenDetail);
        SearchCommand   = new Command(() => Navigation.PushAsync(new SearchPage()));
        ProfileCommand  = new Command(() => Navigation.PushAsync(new ProfilePanelPage()));
        HeroPlayCommand = new Command(OpenHero);

        // Wire the hero + top-bar buttons' D-pad OK straight to their commands.
        // behavior.Command is the reliable path — a gesture binding through
        // x:Reference silently resolves to null on TV.
        HeroFocus.Command    = HeroPlayCommand;
        SearchFocus.Command  = SearchCommand;
        ProfileFocus.Command = ProfileCommand;

        _ = LoadAsync();
    }

    private async void OpenHero()
    {
        if (Hero?.Id is { Length: > 0 } id)
            await Navigation.PushAsync(new NativeDetailPage(id, Hero.Title, Hero.Backdrop));
    }

    private async void OpenDetail(PosterItem? p)
    {
        if (p is null || string.IsNullOrEmpty(p.Id)) return;
        await Navigation.PushAsync(new NativeDetailPage(p.Id, p.Title, p.BackdropUrl));
    }

    private async Task LoadAsync()
    {
        try
        {
            var libraryTask  = _http.GetFromJsonAsync<ApiItem[]>("/api/media?take=500", Json);
            var continueTask = SafeGetAsync<ContinueApi[]>("/api/me/continue?take=20");

            var items = await libraryTask ?? Array.Empty<ApiItem>();
            var cont  = await continueTask ?? Array.Empty<ContinueApi>();

            BuildHero(items, cont);
            BuildRails(items, cont);
        }
        catch { /* leave the page empty on a hard failure */ }
    }

    private async Task<T?> SafeGetAsync<T>(string url)
    {
        try { return await _http.GetFromJsonAsync<T>(url, Json); }
        catch { return default; }
    }

    private void BuildHero(ApiItem[] items, ContinueApi[] cont)
    {
        // Prefer the most-recent continue item as the hero; else the top-rated
        // title that has a backdrop.
        var c = cont.FirstOrDefault(x => !string.IsNullOrEmpty(x.FanartPath ?? x.PosterPath));
        if (c is not null)
        {
            Hero = new HeroVm
            {
                Id       = c.MediaItemId,
                Backdrop = Img(c.FanartPath ?? c.PosterPath!, 1280),
                Eyebrow  = c.IsNew ? "НОВЫЙ ЭПИЗОД" : "ПРОДОЛЖИТЬ",
                Title    = (c.Title ?? "").ToUpperInvariant(),
                Meta     = EpisodeLabel(c.Season, c.Episode),
            };
            return;
        }

        var h = items.Where(i => !string.IsNullOrEmpty(i.FanartPath))
                     .OrderByDescending(i => i.Rating ?? 0)
                     .FirstOrDefault();
        if (h is null) return;
        var meta = new[] { h.Year?.ToString(), h.Rating is > 0 ? $"★ {h.Rating:F1}" : null }
            .Where(s => !string.IsNullOrEmpty(s));
        Hero = new HeroVm
        {
            Id       = h.Id ?? "",
            Backdrop = Img(h.FanartPath!, 1280),
            Eyebrow  = "РЕКОМЕНДУЕМ",
            Title    = (h.Title ?? "").ToUpperInvariant(),
            Meta     = string.Join("   ·   ", meta),
        };
    }

    private void BuildRails(ApiItem[] items, ContinueApi[] cont)
    {
        var rails = new List<RailVm>();

        // 1) Continue watching (with resume bars). Show every row the server
        // returns — don't drop rows missing a poster (they still have a title +
        // fanart fallback), which is what left the rail short vs the web.
        if (cont.Length > 0)
            rails.Add(new RailVm("Продолжить просмотр", cont.Select(ToPosterFromContinue).ToList()));

        // 2) Favorites.
        var favs = items.Where(i => i.IsFavorite && !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                        .Select(ToPoster).ToList();
        if (favs.Count > 0)
            rails.Add(new RailVm("Избранное", favs));

        // 3) One rail per top category (by title count), then an "all" catch-all.
        var withArt = items.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath)).ToList();
        var categories = withArt
            .SelectMany(i => i.CategoryNames ?? Array.Empty<string>())
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(6);

        foreach (var cat in categories)
        {
            var catItems = withArt.Where(i => (i.CategoryNames ?? Array.Empty<string>()).Contains(cat))
                                  .Select(ToPoster).ToList();
            if (catItems.Count >= 3)
                rails.Add(new RailVm(cat, catItems));
        }

        // Fallback: if nothing categorised, show the whole library in one rail.
        if (rails.Count == 0 && withArt.Count > 0)
            rails.Add(new RailVm("Библиотека", withArt.Select(ToPoster).ToList()));

        Rails = rails;
    }

    private string Img(string path, int w) => $"{ImageBase}/api/image?path={Uri.EscapeDataString(path)}&w={w}";

    private static string EpisodeLabel(int? season, int? episode)
    {
        if (season is null && episode is null) return "";
        if (episode is null) return $"Сезон {season}";
        return season is > 0 ? $"S{season} · E{episode}" : $"Эпизод {episode}";
    }

    private PosterItem ToPoster(ApiItem i)
    {
        var path  = i.PosterPath ?? i.FanartPath!;
        var parts = new List<string>();
        if (i.Year is > 0)         parts.Add(i.Year!.Value.ToString());
        if (i.EpisodeCount is > 0) parts.Add($"{i.EpisodeCount} EP");
        if (i.Rating is > 0)       parts.Add($"★ {i.Rating:F1}");

        var p = new PosterItem
        {
            Id          = i.Id ?? "",
            Title       = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl    = Img(path, 330),
            BackdropUrl = string.IsNullOrEmpty(i.FanartPath) ? null : Img(i.FanartPath, 1280),
            TypeLabel   = TypeLabel(i.MediaType),
            Meta        = string.Join("   ·   ", parts),
            Cjk         = i.CjkTitle ?? "",
        };
        p.Open = new Command(() => OpenDetail(p));
        return p;
    }

    private PosterItem ToPosterFromContinue(ContinueApi c)
    {
        var path = c.PosterPath ?? c.FanartPath;
        var p = new PosterItem
        {
            Id          = c.MediaItemId,
            Title       = (c.Title ?? "").ToUpperInvariant(),
            ImageUrl    = string.IsNullOrEmpty(path) ? "" : Img(path, 330),
            BackdropUrl = string.IsNullOrEmpty(c.FanartPath) ? null : Img(c.FanartPath, 1280),
            TypeLabel   = "",
            Meta        = EpisodeLabel(c.Season, c.Episode),
            Cjk         = "",
            Progress    = Math.Clamp(c.Progress, 0, 1),
        };
        p.Open = new Command(() => OpenDetail(p));
        return p;
    }

    private static string TypeLabel(string? t) => t switch
    {
        "Anime"       => "ANIME",
        "Movie"       => "MOVIE",
        "Series"      => "SERIES",
        "Multserials" => "MULTI",
        _             => "",
    };

    // ── API shapes (only the fields the catalog uses) ────────────────────────
    private sealed record ApiItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath, string? CjkTitle,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount,
        bool IsFavorite, string[]? CategoryNames);

    private sealed record ContinueApi(
        string MediaItemId, string? Title, string? PosterPath, string? FanartPath,
        int? Season, int? Episode, double Progress, bool IsNew);

    // ── View models bound from XAML ──────────────────────────────────────────
    public sealed class PosterItem
    {
        public string  Id          { get; init; } = "";
        public string  Title       { get; init; } = "";
        public string  ImageUrl    { get; init; } = "";
        public string? BackdropUrl { get; init; }
        public string  TypeLabel   { get; init; } = "";
        public string  Meta        { get; init; } = "";
        public string  Cjk         { get; init; } = "";
        public double  Progress    { get; init; }          // 0..1; >0 shows the resume bar
        public bool    HasProgress => Progress > 0.01;
        public bool    HasType     => !string.IsNullOrEmpty(TypeLabel);
        /// <summary>D-pad OK action for this card, bound to TvFocusBehavior.Command
        /// from the item's own BindingContext (reliable inside a template).</summary>
        public System.Windows.Input.ICommand? Open { get; set; }
    }

    public sealed class RailVm
    {
        public string Title { get; }
        public ObservableCollection<PosterItem> Items { get; }
        public RailVm(string title, IEnumerable<PosterItem> items)
        {
            Title = title;
            Items = new ObservableCollection<PosterItem>(items);
        }
    }

    public sealed class HeroVm
    {
        public string Id       { get; init; } = "";
        public string Backdrop { get; init; } = "";
        public string Eyebrow  { get; init; } = "";
        public string Title    { get; init; } = "";
        public string Meta     { get; init; } = "";
    }
}
