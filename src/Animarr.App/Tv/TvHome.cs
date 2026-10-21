#if ANDROID
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Leanback.Widget;
using AndroidX.RecyclerView.Widget;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>Back-key hook for the visible native TV screen: "Back → to the top
/// menu first" (TV navigation guideline), then the normal page pop / exit.</summary>
internal static class TvBack
{
    public static Func<bool>? Handler;
}

/// <summary>New TV home: Fluent 2 look, 10-foot sizes, all native.
/// Row 0 = top bar (pill tabs) + immersive hero over a baked-scrim backdrop;
/// then "Продолжить просмотр", "На этой неделе" and one poster rail per
/// library section. One Leanback VerticalGridView of rows; each rail is a
/// HorizontalGridView (focus memory per row, focused card at a fixed key line).</summary>
public sealed class TvHomePage : ContentPage
{
    private TvHomeView? _view;
    private bool _shown;

    public TvHomePage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => _view = new TvHomeView(ctx, this));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        TvBack.Handler = () => _view?.BackToTop() ?? false;
        if (_shown) _view?.OnReturn();
        _shown = true;
        _view?.ResumeCarousel();
    }

    // Android 13+ routes the remote's Back through MAUI (OnBackInvokedDispatcher),
    // not Activity.OnBackPressed — so the "Back → top menu" rule lives here.
    protected override bool OnBackButtonPressed()
        => (_view?.BackToTop() ?? false) || base.OnBackButtonPressed();

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (TvBack.Handler is not null) TvBack.Handler = null;
        _view?.PauseCarousel();
    }
}

internal sealed class TvHomeView : FrameLayout
{
    private readonly TvHomePage _page;
    private readonly TvData _data = new();
    private readonly VerticalGridView _rows;
    private readonly RowsAdapter _adapter;
    private readonly List<object> _rowModels = new();
    private HeroHolder? _hero;

    // hero carousel
    private TvData.ContinueItem[] _feed = Array.Empty<TvData.ContinueItem>();
    private TvData.MediaItem? _featured;
    private int _heroIndex;
    private int _heroIntervalSec = 30;
    private IDispatcherTimer? _timer;
    private DateTime _lastRefresh = DateTime.MinValue;

    // sections
    private TvData.MediaItem[] _library = Array.Empty<TvData.MediaItem>();
    private readonly List<(Guid Id, string Label)> _sections = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sectionFolders = new();

    public TvHomeView(Context ctx, TvHomePage page) : base(ctx)
    {
        _page = page;
        SetBackgroundColor(TvUi.Bg);
        _rows = new VerticalGridView(ctx);
        _rows.SetClipToPadding(false);
        _rows.SetClipChildren(false);
        _rows.SetPadding(0, 0, 0, TvUi.Dp(ctx, 60));
        _rows.WindowAlignment = BaseGridView.WindowAlignBothEdge;
        _rows.WindowAlignmentOffset = TvUi.Dp(ctx, TvUi.SafeY);
        _rows.WindowAlignmentOffsetPercent = BaseGridView.WindowAlignOffsetPercentDisabled;
        _rows.ItemAlignmentOffsetPercent = 0;
        _rows.SetItemAnimator(null);
        _adapter = new RowsAdapter(this);
        _rows.SetAdapter(_adapter);
        AddView(_rows, new LayoutParams(-1, -1));

        _rowModels.Add(HeroRowMarker.Instance);
        _ = LoadAsync();
    }

    // ── Data ─────────────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        try
        {
            var token = TvMedia.EnsureTokenAsync();
            var libT = _data.Get<TvData.MediaItem[]>("/api/media?take=500");
            var contT = _data.Get<TvData.ContinueItem[]>("/api/me/continue?take=12");
            var nextT = _data.Get<TvData.ContinueItem[]>("/api/me/next-up?take=12");
            var weekT = _data.Get<TvData.CalendarEvent[]>("/api/calendar?back=1&ahead=7");
            var cadT = _data.Get<TvData.ConfigValue>(Animarr.Shared.ApiRoutes.AppConfigKey(Animarr.Shared.AppConfigKeys.BackdropIntervalSec));
            var prefT = _data.Get<TvData.Prefs>("/api/me/preferences");
            var secT = TvData.Safe(_data.Api.GetSectionFoldersAsync());
            var folT = TvData.Safe(_data.Api.GetFoldersAsync());

            var prefs = await prefT;
            if (prefs?.AccentHue is int hue) TvUi.SetAccentHue(hue);
            await TvL.LoadAsync(_data.Http, prefs?.Language);
            await token;

            _library = await libT ?? Array.Empty<TvData.MediaItem>();
            if (int.TryParse((await cadT)?.Value, out var iv) && iv > 0) _heroIntervalSec = iv;

            var secs = await secT; var fols = await folT;
            _sections.Clear(); _sectionFolders.Clear();
            if (secs is not null)
                foreach (var s in secs.OrderBy(s => s.Label))
                {
                    _sections.Add((s.Id, s.Label));
                    var ids = fols?.Where(f => f.ParentSectionId == s.Id).Select(f => f.Id).ToHashSet() ?? new();
                    ids.Add(s.Id);
                    _sectionFolders[s.Id] = ids;
                }

            _feed = MergeFeed(await contT, await nextT);
            _featured = _library.Where(i => !string.IsNullOrEmpty(i.FanartPath)).OrderByDescending(i => i.Rating ?? 0).FirstOrDefault();
            _heroIndex = 0;

            BuildRows(await weekT ?? Array.Empty<TvData.CalendarEvent>());
            _lastRefresh = DateTime.UtcNow;
            Post(() => _hero?.FocusPrimary());
            ResumeCarousel();
        }
        catch (Exception ex) { Android.Util.Log.Warn("AnimarrTv", $"home load: {ex.Message}"); }
    }

    private static TvData.ContinueItem[] MergeFeed(TvData.ContinueItem[]? cont, TvData.ContinueItem[]? next)
    {
        var feed = new List<TvData.ContinueItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cont ?? Array.Empty<TvData.ContinueItem>()) if (seen.Add(c.MediaItemId)) feed.Add(c);
        foreach (var n in next ?? Array.Empty<TvData.ContinueItem>()) if (seen.Add(n.MediaItemId)) feed.Add(n);
        return feed.Where(x => !string.IsNullOrEmpty(x.FanartPath ?? x.PosterPath)).Take(12).ToArray();
    }

    /// <summary>Back from detail / player: refresh the resume feed (progress moved).</summary>
    public void OnReturn()
    {
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds < 5) return;
        _lastRefresh = DateTime.UtcNow;
        _ = RefreshFeedAsync();
    }

    private async Task RefreshFeedAsync()
    {
        var cont = await _data.Get<TvData.ContinueItem[]>("/api/me/continue?take=12");
        var next = await _data.Get<TvData.ContinueItem[]>("/api/me/next-up?take=12");
        var feed = MergeFeed(cont, next);
        _feed = feed;
        if (_heroIndex >= _feed.Length) _heroIndex = 0;
        _hero?.Bind(Hero());
        var idx = _rowModels.FindIndex(r => r is RailRow rr && rr.Id == "continue");
        if (idx > 0)
        {
            ((RailRow)_rowModels[idx]).Adapter.Replace(ContinueCards());
        }
    }

    // ── Rows ─────────────────────────────────────────────────────────────────
    private void BuildRows(TvData.CalendarEvent[] week)
    {
        _rowModels.Clear();
        _rowModels.Add(HeroRowMarker.Instance);

        var cont = ContinueCards();
        if (cont.Count > 0) _rowModels.Add(new RailRow("continue", TvL.T("home.next_up", "Смотреть дальше", "Next up"), CardKind.Wide, cont));

        var weekCards = WeekCards(week);
        if (weekCards.Count > 0) _rowModels.Add(new RailRow("week", TvL.T("home.this_week", "На этой неделе", "This week"), CardKind.Wide, weekCards));

        if (_sections.Count > 0)
            foreach (var (id, label) in _sections)
            {
                var ids = _sectionFolders[id];
                var items = _library.Where(i => Guid.TryParse(i.FolderId, out var f) && ids.Contains(f)).ToList();
                if (items.Count > 0) _rowModels.Add(new RailRow("sec:" + id, label, CardKind.Poster, PosterCards(items, id)));
            }
        else if (_library.Length > 0)
            _rowModels.Add(new RailRow("lib", TvL.T("home.library_title", "Библиотека", "Library"), CardKind.Poster, PosterCards(_library.ToList(), null)));

        _adapter.NotifyDataSetChanged();
    }

    private List<CardItem> ContinueCards()
    {
        var list = new List<CardItem>();
        foreach (var c in _feed)
        {
            var ep = c.Episode is int e ? (c.Season is > 0 ? $"S{c.Season} · E{e}" : TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", e)) : "";
            var item = new CardItem
            {
                Kind = CardKind.Wide,
                Title = c.Title ?? "",
                Meta = ep,
                ImageUrl = c.Season is int s && c.Episode is int en
                    ? _data.EpisodeThumb(c.MediaItemId, s, en)
                    : _data.Img(c.FanartPath ?? c.PosterPath, 640),
                Badge = c.IsNew ? TvL.T("home.new_badge", "Новое", "New") : null,
                BadgeAccent = true,
                Progress = c.Progress,
            };
            var id = c.MediaItemId; var title = c.Title;
            item.Click = () => _ = PlayOrOpenAsync(id, title, c.FanartPath);
            item.LongClick = () => OpenDetail(id, title, c.FanartPath);
            list.Add(item);
        }
        return list;
    }

    private List<CardItem> WeekCards(TvData.CalendarEvent[] events)
    {
        var list = new List<CardItem>();
        var weekEnd = DateTime.Now.Date.AddDays(7);
        foreach (var e in events.Where(e => e.AiringAtUtc.ToLocalTime().Date < weekEnd).OrderBy(e => e.AiringAtUtc))
        {
            var local = e.AiringAtUtc.ToLocalTime();
            var art = e.BackdropUrl ?? e.PosterUrl;
            var status = e.Status switch
            {
                "in-library" => TvL.T("calendar.in_library", "В библиотеке", "In library"),
                "aired-waiting" => TvL.T("calendar.aired_waiting", "Вышла — ждём файл", "Aired — awaiting file"),
                _ => TvL.T("calendar.upcoming", "Ожидается", "Upcoming"),
            };
            var id = e.MediaItemId; var title = e.Title;
            list.Add(new CardItem
            {
                Kind = CardKind.Wide,
                Title = e.Title ?? "",
                Meta = $"{TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", e.Episode)} · {status}",
                ImageUrl = string.IsNullOrEmpty(art) ? null : TvMedia.Url($"{_data.Base}{art}&w=640"),
                Badge = $"{DayLabel(local)} · {local:HH:mm}",
                BadgeAccent = local.Date == DateTime.Now.Date,
                Click = () => OpenDetail(id, title, null),
            });
        }
        return list;
    }

    private List<CardItem> PosterCards(List<TvData.MediaItem> items, Guid? section)
    {
        var list = new List<CardItem>();
        foreach (var i in items.Take(20))
        {
            var id = i.Id ?? ""; var title = i.Title; var fan = i.FanartPath;
            list.Add(new CardItem
            {
                Kind = CardKind.Poster,
                Title = i.Title ?? "",
                Meta = TvData.PosterMeta(i),
                ImageUrl = _data.Img(i.PosterPath ?? i.FanartPath, 330),
                Click = () => OpenDetail(id, title, fan),
            });
        }
        if (items.Count > 20)
            list.Add(new CardItem
            {
                Kind = CardKind.SeeAll,
                Title = TvL.T("home.view_all", "Смотреть все", "View all"),
                Meta = items.Count.ToString(),
                Click = () => OpenLibrary(section),
            });
        return list;
    }

    private static string DayLabel(DateTime local)
    {
        var today = DateTime.Now.Date;
        if (local.Date == today) return TvL.T("calendar.today", "Сегодня", "Today");
        if (local.Date == today.AddDays(1)) return TvL.T("calendar.tomorrow", "Завтра", "Tomorrow");
        var days = TvL.T("calendar.weekdays_short", "вс,пн,вт,ср,чт,пт,сб", "Sun,Mon,Tue,Wed,Thu,Fri,Sat").Split(',');
        return days.Length == 7 ? days[(int)local.DayOfWeek] : local.DayOfWeek.ToString()[..3];
    }

    // ── Hero model ───────────────────────────────────────────────────────────
    internal sealed record HeroModel(string Id, string Title, string Eyebrow, string Meta, double Progress,
        string? Backdrop, string PrimaryText, int Count, int Index, string? Fanart);

    internal HeroModel? Hero()
    {
        if (_feed.Length > 0)
        {
            var c = _feed[Math.Clamp(_heroIndex, 0, _feed.Length - 1)];
            var parts = new List<string>();
            if (c.Year is > 0) parts.Add(c.Year.Value.ToString());
            if (c.Season is > 0 && c.Episode is not null) parts.Add($"S{c.Season} · E{c.Episode}");
            else if (c.Episode is not null) parts.Add(TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", c.Episode));
            if (c.Progress > 0.01) parts.Add($"{(int)Math.Round(c.Progress * 100)}%");
            return new HeroModel(c.MediaItemId, c.Title ?? "",
                c.IsNew ? TvL.T("home.new_episode", "Новая серия", "New episode") : TvL.T("home.continue_watching", "Продолжить просмотр", "Continue watching"),
                string.Join("  ·  ", parts), c.Progress, _data.Img(c.FanartPath ?? c.PosterPath, 1280),
                c.Progress > 0.01 ? TvL.T("continue.resume", "Продолжить", "Resume") : TvL.T("home.watch", "Смотреть", "Watch"),
                _feed.Length, _heroIndex, c.FanartPath);
        }
        if (_featured is { } f)
        {
            var meta = new[] { f.Year?.ToString(), f.Rating is > 0 ? $"★ {f.Rating:0.0}" : null, TvData.TypeLabel(f.MediaType) }
                .Where(s => !string.IsNullOrEmpty(s));
            return new HeroModel(f.Id ?? "", f.Title ?? "", TvL.T("home.featured", "Рекомендуем", "Featured"),
                string.Join("  ·  ", meta), 0, _data.Img(f.FanartPath, 1280), TvL.T("home.watch", "Смотреть", "Watch"), 1, 0, f.FanartPath);
        }
        return null;
    }

    internal void StepHero(int delta)
    {
        if (_feed.Length <= 1) return;
        _heroIndex = (_heroIndex + delta + _feed.Length) % _feed.Length;
        _hero?.Bind(Hero());
    }

    public void ResumeCarousel()
    {
        if (_feed.Length <= 1) return;
        _timer ??= _page.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(_heroIntervalSec);
        _timer.Tick -= OnTick; _timer.Tick += OnTick;
        _timer.Start();
    }

    public void PauseCarousel() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e)
    {
        // never rotate under the user's focus — only while they browse the rails
        if (_hero?.HasFocusInside == true) return;
        StepHero(+1);
    }

    // ── Actions ──────────────────────────────────────────────────────────────
    internal void OpenDetail(string id, string? title, string? fanart)
    {
        if (string.IsNullOrEmpty(id)) return;
        _ = _page.Navigation.PushAsync(new TvDetailPage(id, title, fanart));
    }

    internal void OpenLibrary(Guid? section) => _ = _page.Navigation.PushAsync(new TvLibraryPage(section));
    internal void OpenSearch() => _ = _page.Navigation.PushAsync(new TvSearchPage());
    internal void OpenProfile() => _ = _page.Navigation.PushAsync(new ProfilePanelPage());

    internal async Task PlayOrOpenAsync(string id, string? title, string? fanart)
    {
        if (!Guid.TryParse(id, out var g)) return;
        var c = await TvData.Safe(_data.Api.GetContinueAsync(g));
        if (c?.FilePath is { Length: > 0 } path)
            await _page.Navigation.PushAsync(new PlayerPage(g, c.Season, c.Episode, path, title, c.Kind == "continue" ? c.ProgressMs ?? 0 : 0));
        else OpenDetail(id, title, fanart);
    }

    /// <summary>Back → scroll to the top and focus the hero; true when it moved.</summary>
    public bool BackToTop()
    {
        if (_hero?.TabsFocused == true) return false;   // already on the menu → exit
        _rows.SetSelectedPositionSmooth(0);
        _hero?.FocusTabs();
        return true;
    }

    // ── Adapter ──────────────────────────────────────────────────────────────
    private sealed class HeroRowMarker { public static readonly HeroRowMarker Instance = new(); }

    private sealed class RailRow
    {
        public RailRow(string id, string title, CardKind kind, List<CardItem> items)
        { Id = id; Title = title; Kind = kind; Adapter = new CardAdapter(items); }
        public string Id { get; }
        public string Title { get; }
        public CardKind Kind { get; }
        public CardAdapter Adapter { get; }
    }

    private sealed class RowsAdapter : RecyclerView.Adapter
    {
        private readonly TvHomeView _home;
        public RowsAdapter(TvHomeView home) { _home = home; }
        public override int ItemCount => _home._rowModels.Count;
        public override int GetItemViewType(int position)
            => _home._rowModels[position] is RailRow r ? 1 + (int)r.Kind : 0;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var ctx = parent.Context!;
            if (viewType == 0) return _home._hero = new HeroHolder(ctx, _home);
            return new RailHolder(ctx, (CardKind)(viewType - 1));
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            if (holder is HeroHolder h) { _home._hero = h; h.Bind(_home.Hero()); }
            else if (holder is RailHolder r && _home._rowModels[position] is RailRow row) r.Bind(row.Title, row.Adapter);
        }
    }

    private sealed class RailHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _title;
        private readonly HorizontalGridView _grid;
        private readonly CardKind _kind;

        public RailHolder(Context ctx, CardKind kind) : base(TvUi.Column(ctx))
        {
            _kind = kind;
            var col = (LinearLayout)ItemView;
            col.LayoutParameters = new RecyclerView.LayoutParams(-1, -2);
            col.SetClipChildren(false);
            col.SetPadding(0, TvUi.Dp(ctx, 14), 0, 0);
            _title = TvUi.Text_(ctx, TvUi.TSection, TvUi.Text, TvUi.Semibold);
            col.AddView(_title, TvUi.Lp(-2, -2, TvUi.Dp(ctx, TvUi.SafeX), 0, 0, TvUi.Dp(ctx, 2)));
            _grid = Rails.Horizontal(ctx, new CardAdapter(new()), kind);
            col.AddView(_grid);
        }

        public void Bind(string title, CardAdapter adapter)
        {
            _title.Text = title;
            if (_grid.GetAdapter() != adapter) _grid.SetAdapter(adapter);
        }
    }

    /// <summary>Top bar (logo + pill tabs + profile) and the hero, over the
    /// baked-scrim backdrop (one ImageView, no gradient layers).</summary>
    private sealed class HeroHolder : RecyclerView.ViewHolder
    {
        private readonly TvHomeView _home;
        private readonly ImageView _backdrop;
        private readonly TextView _eyebrow, _title, _meta;
        private readonly FrameLayout _progressHost;
        private readonly TextView _primary, _details;
        private readonly LinearLayout _dots;
        private readonly TextView _tabHome;
        private HeroModel? _model;
        private string? _backdropUrl;

        public bool HasFocusInside => ((ViewGroup)ItemView).HasFocus;
        public bool TabsFocused => _bar.HasFocus;
        private readonly LinearLayout _bar;

        public HeroHolder(Context ctx, TvHomeView home) : base(new FrameLayout(ctx))
        {
            _home = home;
            var root = (FrameLayout)ItemView;
            root.LayoutParameters = new RecyclerView.LayoutParams(-1, TvUi.Dp(ctx, 392));
            root.SetClipChildren(false);

            _backdrop = new ImageView(ctx);
            _backdrop.SetScaleType(ImageView.ScaleType.CenterCrop);
            root.AddView(_backdrop, new FrameLayout.LayoutParams(TvUi.Dp(ctx, 680), TvUi.Dp(ctx, 383)) { Gravity = GravityFlags.Top | GravityFlags.End });

            var col = TvUi.Column(ctx);
            col.SetClipChildren(false);
            col.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY), TvUi.Dp(ctx, TvUi.SafeX), 0);
            root.AddView(col, new FrameLayout.LayoutParams(-1, -1));

            // ── top bar ──
            var bar = _bar = TvUi.Row(ctx);
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetClipChildren(false);
            var logo = TvUi.Text_(ctx, 22, TvUi.Text, TvUi.Semibold, 1, "Animarr");
            bar.AddView(logo, TvUi.Lp(-2, -2, 0, 0, TvUi.Dp(ctx, 28)));
            _tabHome = Tab(ctx, TvL.T("nav.home", "Главная", "Home"), selected: true, () => { });
            bar.AddView(_tabHome);
            bar.AddView(Tab(ctx, TvL.T("home.library_title", "Библиотека", "Library"), false, () => home.OpenLibrary(null)), TvUi.Lp(-2, -2, TvUi.Dp(ctx, 8)));
            bar.AddView(Tab(ctx, TvL.T("search.title", "Поиск", "Search"), false, home.OpenSearch), TvUi.Lp(-2, -2, TvUi.Dp(ctx, 8)));
            bar.AddView(new AView(ctx), new LinearLayout.LayoutParams(0, 1, 1f));
            bar.AddView(Tab(ctx, TvL.T("profile.title", "Профиль", "Profile"), false, home.OpenProfile, solid: true));
            col.AddView(bar, new LinearLayout.LayoutParams(-1, TvUi.Dp(ctx, 40)));

            // ── hero text ──
            _eyebrow = TvUi.Text_(ctx, 14, TvUi.AccentHi, TvUi.Medium);
            col.AddView(_eyebrow, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 66)));
            _title = TvUi.Text_(ctx, TvUi.TDisplay, TvUi.Text, TvUi.Semibold, 2);
            _title.SetLineSpacing(0, 1.05f);
            col.AddView(_title, TvUi.Lp(TvUi.Dp(ctx, 520), -2, 0, TvUi.Dp(ctx, 8)));
            _meta = TvUi.Text_(ctx, 15, TvUi.TextDim, TvUi.Regular);
            col.AddView(_meta, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 10)));
            _progressHost = new FrameLayout(ctx);
            col.AddView(_progressHost, TvUi.Lp(TvUi.Dp(ctx, 300), TvUi.Dp(ctx, 4), 0, TvUi.Dp(ctx, 12)));

            var buttons = TvUi.Row(ctx);
            buttons.SetClipChildren(false);
            _primary = TvUi.Button(ctx, "", true, () => { if (_model is { } m) _ = home.PlayOrOpenAsync(m.Id, m.Title, m.Fanart); });
            _details = TvUi.Button(ctx, TvL.T("home.details", "Подробнее", "Details"), false, () => { if (_model is { } m) home.OpenDetail(m.Id, m.Title, m.Fanart); });
            buttons.AddView(_primary, TvUi.Lp(-2, TvUi.Dp(ctx, 44)));
            buttons.AddView(_details, TvUi.Lp(-2, TvUi.Dp(ctx, 44), TvUi.Dp(ctx, 12)));
            col.AddView(buttons, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 20)));

            // LEFT on the first button / RIGHT on the last flips the carousel
            _primary.KeyPress += (_, e) =>
            {
                if (e.Event?.Action == KeyEventActions.Down && e.KeyCode == Keycode.DpadLeft && (_model?.Count ?? 0) > 1) { home.StepHero(-1); e.Handled = true; }
                else e.Handled = false;
            };
            _details.KeyPress += (_, e) =>
            {
                if (e.Event?.Action == KeyEventActions.Down && e.KeyCode == Keycode.DpadRight && (_model?.Count ?? 0) > 1) { home.StepHero(+1); e.Handled = true; }
                else e.Handled = false;
            };

            _dots = TvUi.Row(ctx);
            col.AddView(_dots, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 18)));
        }

        private static TextView Tab(Context ctx, string text, bool selected, Action click, bool solid = false)
        {
            var t = TvUi.Text_(ctx, 15, selected ? TvUi.Text : TvUi.TextDim, TvUi.Medium, 1, text);
            t.Gravity = GravityFlags.Center;
            int hp = TvUi.Dp(ctx, 16);
            t.SetPadding(hp, 0, hp, 0);
            t.Focusable = true; t.Clickable = true; t.FocusableInTouchMode = false;
            var normal = selected || solid ? TvUi.Round(ctx, TvUi.Surface2, 20) : null;
            var focus = TvUi.Round(ctx, AColor.White, 20);
            t.Background = normal;
            t.FocusChange += (_, e) =>
            {
                t.Background = e.HasFocus ? focus : normal;
                t.SetTextColor(e.HasFocus ? AColor.ParseColor("#1a1a1a") : (selected || solid ? TvUi.Text : TvUi.TextDim));
            };
            t.Click += (_, _) => click();
            t.LayoutParameters = new LinearLayout.LayoutParams(-2, -1);
            return t;
        }

        public void Bind(HeroModel? m)
        {
            _model = m;
            var ctx = ItemView.Context!;
            if (m is null) { _title.Text = ""; _eyebrow.Text = ""; _meta.Text = ""; _primary.Visibility = _details.Visibility = ViewStates.Invisible; return; }
            _primary.Visibility = _details.Visibility = ViewStates.Visible;
            _eyebrow.Text = m.Eyebrow;
            _eyebrow.SetTextColor(TvUi.AccentHi);
            _title.Text = m.Title;
            _meta.Text = m.Meta;
            _primary.Text = "▶  " + m.PrimaryText;
            _progressHost.RemoveAllViews();
            if (m.Progress > 0.01)
                _progressHost.AddView(TvUi.Progress(ctx, TvUi.Dp(ctx, 300), m.Progress, TvUi.AccentHi));
            if (m.Backdrop != _backdropUrl)
            {
                _backdropUrl = m.Backdrop;
                // 960×540 px decode: under the scrim the difference to full-res is invisible
                TvImages.Backdrop(_backdrop, m.Backdrop, 960, 540);
            }
            _dots.RemoveAllViews();
            if (m.Count > 1)
                for (int i = 0; i < m.Count; i++)
                {
                    var on = i == m.Index;
                    var d = new AView(ctx) { Background = TvUi.Round(ctx, on ? TvUi.AccentHi : AColor.Argb(0x66, 0xff, 0xff, 0xff), 3) };
                    _dots.AddView(d, TvUi.Lp(TvUi.Dp(ctx, on ? 20 : 8), TvUi.Dp(ctx, 6), 0, 0, TvUi.Dp(ctx, 6)));
                }
        }

        public void FocusPrimary() { if (_primary.Visibility == ViewStates.Visible) _primary.RequestFocus(); }
        public void FocusTabs() => _tabHome.RequestFocus();
    }
}
#endif
