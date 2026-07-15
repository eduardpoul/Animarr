using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Input;
using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Native TV home, styled after the web catalog: a cinematic hero (resume CTA +
/// progress) over "Смотреть дальше" (landscape continue cards) and "Библиотека"
/// (poster rail). Minimal top bar — search + profile only; the TV deliberately
/// omits downloads / settings / stats / favorites / recommendations. Each card is
/// a native D-pad focus target (TvFocusBehavior) whose OK opens the detail page.
/// Talks to the server through the shared cookie-carrying HttpClient.
/// </summary>
public partial class CatalogNativePage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private readonly IAnimarrApiClient _api;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    // Section/source filter (web topbar pills): All + FolderWatcher sections.
    private ApiItem[] _libraryItems = Array.Empty<ApiItem>();
    private List<(Guid Id, string Label)> _sections = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sectionFolderIds = new();
    private Guid? _activeSection;

    public static readonly BindableProperty HeroProperty =
        BindableProperty.Create(nameof(Hero), typeof(HeroVm), typeof(CatalogNativePage));
    public HeroVm? Hero { get => (HeroVm?)GetValue(HeroProperty); set => SetValue(HeroProperty, value); }

    public ICommand OpenCommand     { get; }
    public ICommand SearchCommand   { get; }
    public ICommand ProfileCommand  { get; }
    public ICommand HeroPlayCommand { get; }

    // Hero carousel state — the web CatalogContinueHero rotates the merged feed
    // on a server-configured cadence and exposes ‹ › chevrons + dashes (pager G).
    private ContinueApi[]     _heroFeed = Array.Empty<ContinueApi>();
    private int               _heroIndex;
    private IDispatcherTimer? _heroTimer;
    private int               _heroIntervalSec = 30;   // web default; overridden by app-config

    public CatalogNativePage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();
        _api  = services.GetRequiredService<IAnimarrApiClient>();

        OpenCommand     = new Command<PosterItem>(OpenDetail);
        SearchCommand   = new Command(() => Navigation.PushAsync(new SearchPage()));
        ProfileCommand  = new Command(() => Navigation.PushAsync(new ProfilePanelPage()));
        HeroPlayCommand = new Command(OpenHero);

        // behavior.Command is the reliable D-pad path — a gesture binding through
        // x:Reference silently resolves to null on TV.
        HeroFocus.Command     = HeroPlayCommand;
        SearchFocus.Command   = SearchCommand;
        ProfileFocus.Command  = ProfileCommand;
        HeroPrevFocus.Command = new Command(() => StepHero(-1));
        HeroNextFocus.Command = new Command(() => StepHero(+1));

        _ = LoadAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_heroFeed.Length > 1) StartHeroTimer();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _heroTimer?.Stop();
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
            var continueTask = SafeGetAsync<ContinueApi[]>("/api/me/continue?take=12");
            var nextUpTask   = SafeGetAsync<ContinueApi[]>("/api/me/next-up?take=12");
            // "На этой неделе" — same window the web Home requests.
            var weekTask     = SafeGetAsync<CalendarEventApi[]>("/api/calendar?back=1&ahead=7");
            // Carousel cadence from server config — same key the web Home reads.
            var cadenceTask  = SafeGetAsync<ConfigValueApi>(
                Animarr.Shared.ApiRoutes.AppConfigKey(Animarr.Shared.AppConfigKeys.BackdropIntervalSec));
            var prefsTask    = SafeGetAsync<PrefsApi>("/api/me/preferences");
            var sectionsTask = SafeAsync(_api.GetSectionFoldersAsync());
            var foldersTask  = SafeAsync(_api.GetFoldersAsync());

            // The lang pack must be in before any text is built.
            await TvL.LoadAsync(_http, (await prefsTask)?.Language);
            ApplyStrings();

            var items = await libraryTask  ?? Array.Empty<ApiItem>();
            var cont  = await continueTask ?? Array.Empty<ContinueApi>();
            var next  = await nextUpTask   ?? Array.Empty<ContinueApi>();
            if (int.TryParse((await cadenceTask)?.Value, out var iv) && iv > 0)
                _heroIntervalSec = iv;

            // Sections (source folders) + their child-folder ids for filtering.
            var sections = await sectionsTask;
            var folders  = await foldersTask;
            _sections = sections?.OrderBy(s => s.Label)
                                 .Select(s => (s.Id, (string)s.Label))
                                 .ToList() ?? new();
            _sectionFolderIds.Clear();
            if (sections is not null)
            {
                foreach (var sec in sections)
                {
                    var ids = folders?.Where(f => f.ParentSectionId == sec.Id)
                                      .Select(f => f.Id).ToHashSet() ?? new HashSet<Guid>();
                    ids.Add(sec.Id);
                    _sectionFolderIds[sec.Id] = ids;
                }
            }
            _libraryItems = items;
            BuildSections();

            // Same merged feed as the web Home: in-progress "continue" rows come
            // first and win on dedup; next-up rows (next-in-line / freshly
            // landed) follow. The hero shows feed[0], the rail lists the feed.
            var feed = new List<ContinueApi>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cont) if (seen.Add(c.MediaItemId)) feed.Add(c);
            foreach (var n in next) if (seen.Add(n.MediaItemId)) feed.Add(n);
            var feedArr = feed.Take(12).ToArray();

            BuildHero(items, feedArr);
            BuildContinue(feedArr);
            BuildWeek(await weekTask ?? Array.Empty<CalendarEventApi>());
            BuildLibrary(FilteredLibrary());
        }
        catch { /* leave the page empty on a hard failure */ }
    }

    /// <summary>Static texts that depend on the loaded lang pack.</summary>
    private void ApplyStrings()
    {
        ContinueTitle.Text = TvL.T("home.next_up", "Смотреть дальше", "Next up");
        WeekTitle.Text     = TvL.T("home.this_week", "На этой неделе", "This week");
        LibraryTitle.Text  = TvL.T("home.library_title", "Библиотека", "Library");
    }

    // ── Section/source filter (web topbar folder pills) ─────────────────────
    private void BuildSections()
    {
        SectionsHost.Children.Clear();
        if (_sections.Count == 0) return;
        SectionsHost.Children.Add(TvCards.BuildFilterChip(
            TvL.T("catalog.tab_all", "Все", "All"), _activeSection is null,
            new Command(() => SwitchSection(null))));
        foreach (var (id, label) in _sections)
        {
            var sid = id;
            SectionsHost.Children.Add(TvCards.BuildFilterChip(
                label, _activeSection == sid, new Command(() => SwitchSection(sid))));
        }
    }

    private void SwitchSection(Guid? id)
    {
        if (_activeSection == id) return;
        _activeSection = id;
        BuildSections();
        BuildLibrary(FilteredLibrary());
    }

    private ApiItem[] FilteredLibrary()
    {
        if (_activeSection is not Guid sec) return _libraryItems;
        var ids = _sectionFolderIds.GetValueOrDefault(sec);
        if (ids is null) return _libraryItems;
        return _libraryItems.Where(i => Guid.TryParse(i.FolderId, out var f) && ids.Contains(f))
                            .ToArray();
    }

    private async Task<T?> SafeGetAsync<T>(string url)
    {
        try { return await _http.GetFromJsonAsync<T>(url, Json); }
        catch { return default; }
    }

    private static async Task<T?> SafeAsync<T>(Task<T> task)
    {
        try { return await task; } catch { return default; }
    }

    private void BuildHero(ApiItem[] items, ContinueApi[] cont)
    {
        // Web CatalogContinueHero: the hero rotates the whole merged feed, with
        // a pager to flip manually. Falls back to the top-rated backdrop title
        // (static) when nothing is in progress. Titles keep their natural case.
        _heroFeed = cont.Where(x => !string.IsNullOrEmpty(x.FanartPath ?? x.PosterPath))
                        .ToArray();
        if (_heroFeed.Length > 0)
        {
            _heroIndex = 0;
            ShowHeroSlide();
            HeroPager.IsVisible = _heroFeed.Length > 1;
            StartHeroTimer();
            return;
        }

        HeroPager.IsVisible = false;
        var h = items.Where(i => !string.IsNullOrEmpty(i.FanartPath))
                     .OrderByDescending(i => i.Rating ?? 0)
                     .FirstOrDefault();
        if (h is null) return;
        var meta = new[] { h.Year?.ToString(), h.Rating is > 0 ? $"★ {h.Rating:F1}" : null }
            .Where(s => !string.IsNullOrEmpty(s));
        Hero = new HeroVm
        {
            Id         = h.Id ?? "",
            Backdrop   = Img(h.FanartPath!, 1280),
            Eyebrow    = TvL.T("home.featured", "Рекомендуем", "Featured").ToUpperInvariant(),
            Title      = h.Title ?? "",
            MetaLine   = string.Join("  ", meta),
            ButtonText = "▶   " + TvL.T("home.watch", "Смотреть", "Watch"),
        };
    }

    /// <summary>Bind the current feed slot into the hero + repaint the pager
    /// dashes (active = wide orange, like the web dash pager).</summary>
    private void ShowHeroSlide()
    {
        var c = _heroFeed[_heroIndex];
        // Compact web-style meta: "2022  S03  E12" (+ orange percent label).
        var parts = new List<string>();
        if (c.Year is > 0)    parts.Add(c.Year!.Value.ToString());
        if (c.Season is > 0)  parts.Add($"S{c.Season:00}");
        if (c.Episode is not null) parts.Add($"E{c.Episode}");
        Hero = new HeroVm
        {
            Id         = c.MediaItemId,
            Backdrop   = Img(c.FanartPath ?? c.PosterPath!, 1280),
            Eyebrow    = (c.IsNew
                ? TvL.T("home.new_episode", "Новая серия", "New episode")
                : TvL.T("home.continue_watching", "Продолжить просмотр", "Continue watching"))
                .ToUpperInvariant(),
            Title      = c.Title ?? "",
            MetaLine   = string.Join("  ", parts),
            Percent    = c.Progress > 0.01 ? $"{(int)Math.Round(c.Progress * 100)}%" : "",
            Progress   = Math.Clamp(c.Progress, 0, 1),
            ButtonText = "▶   " + TvL.T("continue.resume", "Продолжить", "Resume"),
        };

        HeroDashes.Children.Clear();
        if (_heroFeed.Length <= 1) return;
        for (var i = 0; i < _heroFeed.Length; i++)
        {
            var active = i == _heroIndex;
            HeroDashes.Children.Add(new BoxView
            {
                WidthRequest    = active ? 26 : 16,
                HeightRequest   = 3,
                CornerRadius    = 1.5,
                Color           = active ? Color.FromArgb("#e8772e") : Color.FromArgb("#4DFFFFFF"),
                VerticalOptions = LayoutOptions.Center,
            });
        }
    }

    /// <summary>‹ › chevron press: flip with wrap-around and reset the auto-
    /// rotate cadence so a manual step doesn't immediately jump again.</summary>
    private void StepHero(int delta)
    {
        if (_heroFeed.Length <= 1) return;
        _heroIndex = (_heroIndex + delta + _heroFeed.Length) % _heroFeed.Length;
        ShowHeroSlide();
        _heroTimer?.Stop();
        StartHeroTimer();
    }

    private void StartHeroTimer()
    {
        if (_heroFeed.Length <= 1) return;
        _heroTimer ??= Dispatcher.CreateTimer();
        _heroTimer.Interval = TimeSpan.FromSeconds(_heroIntervalSec);
        _heroTimer.Tick -= OnHeroTick;
        _heroTimer.Tick += OnHeroTick;
        _heroTimer.Start();
    }

    private void OnHeroTick(object? sender, EventArgs e)
    {
        _heroIndex = (_heroIndex + 1) % _heroFeed.Length;
        ShowHeroSlide();
    }

    private void BuildContinue(ContinueApi[] cont)
    {
        ContinueHost.Children.Clear();
        var count = 0;
        foreach (var c in cont)
        {
            if (string.IsNullOrEmpty(c.FanartPath ?? c.PosterPath)) continue;
            var p = ToContinueCard(c);
            var card = TvCards.BuildContinueCard(p, out var img);
            // Fanart shows instantly; the episode's own frame swaps in when the
            // authenticated download lands (Image.Source can't send the cookie,
            // and on servers without the anonymous thumb endpoint it 401s).
            if (!string.IsNullOrEmpty(p.ImageUrl)) img.Source = p.ImageUrl;
            if (!string.IsNullOrEmpty(p.ThumbPath)) _ = LoadThumbAsync(img, p.ThumbPath!);
            ContinueHost.Children.Add(card);
            count++;
        }
        ContinueSection.IsVisible = count > 0;
    }

    /// <summary>Download an episode frame through the cookie-carrying HttpClient
    /// and swap it into <paramref name="img"/>. Failures keep the fanart.</summary>
    private async Task LoadThumbAsync(Image img, string relativeUrl)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(relativeUrl);
            if (bytes.Length == 0) return;
            MainThread.BeginInvokeOnMainThread(() =>
                img.Source = ImageSource.FromStream(() => new MemoryStream(bytes)));
        }
        catch { /* keep the fanart fallback */ }
    }

    /// <summary>"На этой неделе" — the web ThisWeekRail: airing events within the
    /// coming week, ordered by air time. Collapses when the week is empty.</summary>
    private void BuildWeek(CalendarEventApi[] events)
    {
        WeekHost.Children.Clear();
        var weekEnd = DateTime.Now.Date.AddDays(7);
        var count = 0;
        foreach (var e in events
                     .Where(e => e.AiringAtUtc.ToLocalTime().Date < weekEnd)
                     .OrderBy(e => e.AiringAtUtc))
        {
            var local = e.AiringAtUtc.ToLocalTime();
            var art   = e.BackdropUrl ?? e.PosterUrl;
            var img   = string.IsNullOrEmpty(art) ? null : $"{ImageBase}{art}&w=640";
            var title = e.Title ?? "";
            var open  = new Command(async () => await Navigation.PushAsync(
                new NativeDetailPage(e.MediaItemId, title,
                    string.IsNullOrEmpty(e.BackdropUrl) ? null : $"{ImageBase}{e.BackdropUrl}&w=1280")));
            WeekHost.Children.Add(TvCards.BuildWeekCard(
                title, $"{DayLabel(local)} · {local:HH:mm}", $"EP {e.Episode}",
                e.Status ?? "upcoming", StatusLabel(e.Status), img, open));
            count++;
        }
        WeekSection.IsVisible = count > 0;
    }

    private static string DayLabel(DateTime local)
    {
        var today = DateTime.Now.Date;
        if (local.Date == today) return TvL.T("calendar.today", "Сегодня", "Today");
        if (local.Date == today.AddDays(1)) return TvL.T("calendar.tomorrow", "Завтра", "Tomorrow");
        // Sunday-first, matching the web lang pack's calendar.weekdays_short.
        var days = TvL.T("calendar.weekdays_short", "вс,пн,вт,ср,чт,пт,сб",
                         "Sun,Mon,Tue,Wed,Thu,Fri,Sat").Split(',');
        return days.Length == 7 ? days[(int)local.DayOfWeek] : local.DayOfWeek.ToString()[..3];
    }

    private static string StatusLabel(string? status) => status switch
    {
        "in-library"    => TvL.T("calendar.in_library", "В библиотеке", "In library"),
        "aired-waiting" => TvL.T("calendar.aired_waiting", "Вышла — ждём файл", "Aired — awaiting file"),
        _               => TvL.T("calendar.upcoming", "Ожидается", "Upcoming"),
    };

    private void BuildLibrary(ApiItem[] items)
    {
        // Web-style library block: an alphabetical two-row grid preview with a
        // trailing "Просмотреть все" tile that opens the full-library page.
        // 7 columns wrap in the content strip → 13 posters + the tile = 2 rows.
        const int previewCount = 13;
        var all = items.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                       .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                       .ToList();

        LibraryHost.Children.Clear();
        foreach (var p in all.Take(previewCount).Select(ToPoster))
            LibraryHost.Children.Add(TvCards.BuildPosterCard(p));
        if (all.Count > 0)
        {
            // The full-library page opens with the same source filter active.
            LibraryHost.Children.Add(TvCards.BuildSeeAllTile(all.Count,
                new Command(() => Navigation.PushAsync(new LibraryAllPage(_activeSection)))));
        }
        LibrarySection.IsVisible = _libraryItems.Length > 0;
    }

    private string Img(string path, int w) => $"{ImageBase}/api/image?path={Uri.EscapeDataString(path)}&w={w}";

    private static string EpisodeLabel(int? season, int? episode)
    {
        if (season is null && episode is null) return "";
        if (episode is null) return $"Сезон {season}";
        return season is > 0 ? $"S{season} · E{episode}" : $"Эпизод {episode}";
    }

    // Library poster: upper-cased title (matches the web library cards) + a meta
    // line "2022 · 40 EP · ★ 10.0" + a type badge.
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
            Meta        = string.Join(" · ", parts),
        };
        p.Open = new Command(() => OpenDetail(p));
        return p;
    }

    // "Смотреть дальше" landscape card: fanart shows instantly (ImageUrl); the
    // EPISODE's own frame (ThumbPath — same episode-thumb endpoint the web rail
    // uses) is swapped in by LoadThumbAsync. Resume bar + НОВОЕ badge.
    private PosterItem ToContinueCard(ContinueApi c)
    {
        var fan = c.FanartPath ?? c.PosterPath;
        var p = new PosterItem
        {
            Id          = c.MediaItemId,
            Title       = c.Title ?? "",
            ImageUrl    = string.IsNullOrEmpty(fan) ? "" : Img(fan!, 640),
            ThumbPath   = c.Season is int s && c.Episode is int ep
                        ? $"/api/media/{c.MediaItemId}/episode-thumb?season={s}&episode={ep}"
                        : null,
            BackdropUrl = string.IsNullOrEmpty(c.FanartPath) ? null : Img(c.FanartPath, 1280),
            Meta        = EpisodeLabel(c.Season, c.Episode),
            Progress    = Math.Clamp(c.Progress, 0, 1),
            IsNew       = c.IsNew,
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
        bool IsFavorite, string[]? CategoryNames, string? FolderId);

    private sealed record PrefsApi(string? Language, string? EpisodeListView);

    private sealed record ContinueApi(
        string MediaItemId, string? Title, string? PosterPath, string? FanartPath,
        int? Year, int? Season, int? Episode, double Progress, bool IsNew);

    private sealed record ConfigValueApi(string? Value);

    private sealed record CalendarEventApi(
        string MediaItemId, string? Title, string? PosterUrl, string? BackdropUrl,
        int Episode, DateTime AiringAtUtc, string? Status);

    // ── View models bound from XAML ──────────────────────────────────────────
    public sealed class PosterItem
    {
        public string  Id          { get; init; } = "";
        public string  Title       { get; init; } = "";
        public string  ImageUrl    { get; init; } = "";
        /// <summary>Relative episode-thumb URL, downloaded via the authenticated
        /// HttpClient (see LoadThumbAsync) — Image.Source can't send the cookie.</summary>
        public string? ThumbPath   { get; init; }
        public string? BackdropUrl { get; init; }
        public string  TypeLabel   { get; init; } = "";
        public string  Meta        { get; init; } = "";
        public double  Progress    { get; init; }          // 0..1; >0 shows the resume bar
        public bool    IsNew       { get; init; }
        public bool    HasProgress => Progress > 0.01;
        /// <summary>Pixel width of the hand-rolled resume fill (card is 200 wide).</summary>
        public double  ProgressWidth => Math.Round(200 * Progress);
        public bool    HasType     => !string.IsNullOrEmpty(TypeLabel);
        public bool    HasMeta     => !string.IsNullOrEmpty(Meta);
        /// <summary>D-pad OK action for this card, bound to TvFocusBehavior.Command
        /// from the item's own BindingContext (reliable inside a template).</summary>
        public System.Windows.Input.ICommand? Open { get; set; }
    }

    public sealed class HeroVm
    {
        public string Id         { get; init; } = "";
        public string Backdrop   { get; init; } = "";
        public string Eyebrow    { get; init; } = "";
        public string Title      { get; init; } = "";
        public string MetaLine   { get; init; } = "";       // "2022  S03  E12"
        public string Percent    { get; init; } = "";       // "36%", orange
        public string ButtonText { get; init; } = "▶   Смотреть";
        public double Progress   { get; init; }
        public bool   HasPercent  => !string.IsNullOrEmpty(Percent);
        public bool   HasProgress => Progress > 0.01;
        /// <summary>Pixel width of the hand-rolled resume fill (track is 330 wide).</summary>
        public double ProgressWidth => Math.Round(330 * Progress);
    }
}
