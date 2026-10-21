#if ANDROID
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Leanback.Widget;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>Full library: section pills + a virtualised 6-column poster grid
/// (Leanback VerticalGridView). Replaces the FlexLayout page whose 400 MAUI
/// cards took seconds to build and re-uploaded every texture on each frame.</summary>
public sealed class TvLibraryPage : ContentPage
{
    public TvLibraryPage(Guid? section = null)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => new TvLibraryView(ctx, this, section));
    }
}

internal static class TvGrid
{
    /// <summary>6-column poster grid sized to the TV layout grid.</summary>
    public static VerticalGridView Posters(Context ctx, CardAdapter adapter)
    {
        var g = new VerticalGridView(ctx);
        g.SetNumColumns(6);
        g.SetColumnWidth(TvUi.Dp(ctx, TvUi.PosterW));
        g.HorizontalSpacing = TvUi.Dp(ctx, TvUi.Gap);
        g.VerticalSpacing = TvUi.Dp(ctx, 14);
        g.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 14), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 40));
        g.SetClipToPadding(false);
        g.SetClipChildren(false);
        g.WindowAlignment = BaseGridView.WindowAlignBothEdge;
        g.WindowAlignmentOffsetPercent = 35f;
        g.ItemAlignmentOffsetPercent = 0;
        g.SetItemAnimator(null);
        g.SetRecycledViewPool(Rails.SharedPool);
        g.HasFixedSize = true;
        g.SetAdapter(adapter);
        return g;
    }

    public static TextView Pill(Context ctx, string text, bool active, Action click)
    {
        var t = TvUi.Text_(ctx, 15, active ? TvUi.Text : TvUi.TextDim, TvUi.Medium, 1, text);
        t.Gravity = GravityFlags.Center;
        int hp = TvUi.Dp(ctx, 16);
        t.SetPadding(hp, 0, hp, 0);
        t.Focusable = true; t.Clickable = true; t.FocusableInTouchMode = false;
        var normal = active ? TvUi.Round(ctx, TvUi.Accent, 20) : TvUi.Round(ctx, TvUi.Surface2, 20);
        var focus = TvUi.Round(ctx, AColor.White, 20);
        t.Background = normal;
        t.FocusChange += (_, e) =>
        {
            t.Background = e.HasFocus ? focus : normal;
            t.SetTextColor(e.HasFocus ? AColor.ParseColor("#1a1a1a") : (active ? AColor.White : TvUi.TextDim));
        };
        t.Click += (_, _) => click();
        return t;
    }
}

internal sealed class TvLibraryView : LinearLayout
{
    private readonly ContentPage _page;
    private readonly TvData _data = new();
    private readonly CardAdapter _adapter = new(new());
    private readonly VerticalGridView _grid;
    private readonly LinearLayout _pills;
    private readonly TextView _count;
    private TvData.MediaItem[] _items = Array.Empty<TvData.MediaItem>();
    private readonly List<(Guid Id, string Label)> _sections = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sectionFolders = new();
    private Guid? _active;

    public TvLibraryView(Context ctx, ContentPage page, Guid? section) : base(ctx)
    {
        _page = page; _active = section;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(TvUi.Bg);
        SetClipChildren(false);

        var head = TvUi.Row(ctx);
        head.SetGravity(GravityFlags.Bottom);
        head.AddView(TvUi.Text_(ctx, TvUi.THeadline + 4, TvUi.Text, TvUi.Semibold, 1, TvL.T("home.library_title", "Библиотека", "Library")));
        _count = TvUi.Text_(ctx, 15, TvUi.TextFaint);
        head.AddView(_count, TvUi.Lp(-2, -2, TvUi.Dp(ctx, 14), 0, 0, TvUi.Dp(ctx, 4)));
        AddView(head, TvUi.Lp(-1, -2, TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY), TvUi.Dp(ctx, TvUi.SafeX)));

        _pills = TvUi.Row(ctx);
        _pills.SetClipChildren(false);
        AddView(_pills, TvUi.Lp(-1, TvUi.Dp(ctx, 40), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 16), TvUi.Dp(ctx, TvUi.SafeX)));

        _grid = TvGrid.Posters(ctx, _adapter);
        AddView(_grid, new LinearLayout.LayoutParams(-1, 0, 1f));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var token = TvMedia.EnsureTokenAsync();
        var itemsT = _data.Get<TvData.MediaItem[]>("/api/media?take=1000");
        var secT = TvData.Safe(_data.Api.GetSectionFoldersAsync());
        var folT = TvData.Safe(_data.Api.GetFoldersAsync());
        await token;
        _items = (await itemsT ?? Array.Empty<TvData.MediaItem>())
            .Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        var secs = await secT; var fols = await folT;
        if (secs is not null)
            foreach (var s in secs.OrderBy(s => s.Label))
            {
                _sections.Add((s.Id, s.Label));
                var ids = fols?.Where(f => f.ParentSectionId == s.Id).Select(f => f.Id).ToHashSet() ?? new();
                ids.Add(s.Id);
                _sectionFolders[s.Id] = ids;
            }
        if (_active is Guid a && !_sectionFolders.ContainsKey(a)) _active = null;
        Apply(focusGrid: true);
    }

    private void Apply(bool focusGrid)
    {
        var ctx = Context!;
        _pills.RemoveAllViews();
        if (_sections.Count > 0)
        {
            _pills.AddView(TvGrid.Pill(ctx, TvL.T("catalog.tab_all", "Все", "All"), _active is null, () => Switch(null)), TvUi.Lp(-2, -1, 0, 0, TvUi.Dp(ctx, 10)));
            foreach (var (id, label) in _sections)
            {
                var sid = id;
                _pills.AddView(TvGrid.Pill(ctx, label, _active == sid, () => Switch(sid)), TvUi.Lp(-2, -1, 0, 0, TvUi.Dp(ctx, 10)));
            }
        }
        IEnumerable<TvData.MediaItem> src = _items;
        if (_active is Guid sec && _sectionFolders.TryGetValue(sec, out var ids))
            src = src.Where(i => Guid.TryParse(i.FolderId, out var f) && ids.Contains(f));
        var cards = src.Select(i =>
        {
            var id = i.Id ?? ""; var title = i.Title; var fan = i.FanartPath;
            return new CardItem
            {
                Kind = CardKind.Poster, Title = i.Title ?? "", Meta = TvData.PosterMeta(i),
                ImageUrl = _data.Img(i.PosterPath ?? i.FanartPath, 330),
                Click = () => _ = _page.Navigation.PushAsync(new TvDetailPage(id, title, fan)),
            };
        }).ToList();
        _count.Text = cards.Count.ToString();
        _adapter.Replace(cards);
        if (focusGrid) _grid.Post(() => { _grid.SelectedPosition = 0; _grid.RequestFocus(); });
    }

    private void Switch(Guid? id)
    {
        if (_active == id) return;
        _active = id;
        Apply(focusGrid: false);
        // keep focus on the pill row (the pills were rebuilt)
        var idx = id is null ? 0 : 1 + _sections.FindIndex(s => s.Id == id);
        _pills.GetChildAt(Math.Max(0, idx))?.RequestFocus();
    }
}

/// <summary>Search: one text box (the TV keyboard opens on OK) + the same
/// virtualised poster grid. Debounced 350 ms.</summary>
public sealed class TvSearchPage : ContentPage
{
    public TvSearchPage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => new TvSearchView(ctx, this));
    }
}

internal sealed class TvSearchView : LinearLayout
{
    private readonly ContentPage _page;
    private readonly TvData _data = new();
    private readonly CardAdapter _adapter = new(new());
    private readonly EditText _box;
    private readonly TextView _status;
    private CancellationTokenSource? _cts;

    public TvSearchView(Context ctx, ContentPage page) : base(ctx)
    {
        _page = page;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(TvUi.Bg);

        AddView(TvUi.Text_(ctx, TvUi.THeadline + 4, TvUi.Text, TvUi.Semibold, 1, TvL.T("search.title", "Поиск", "Search")),
            TvUi.Lp(-2, -2, TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY)));

        _box = new EditText(ctx)
        {
            Hint = TvL.T("search.placeholder", "Название аниме, сериала или фильма", "Title of an anime, series or movie"),
            InputType = Android.Text.InputTypes.ClassText,
            ImeOptions = Android.Views.InputMethods.ImeAction.Search,
        };
        _box.SetSingleLine(true);
        _box.SetTextSize(Android.Util.ComplexUnitType.Sp, 20);
        _box.SetTextColor(TvUi.Text);
        _box.SetHintTextColor(TvUi.TextFaint);
        _box.Typeface = TvUi.Regular;
        int p = TvUi.Dp(ctx, 16);
        _box.SetPadding(p, 0, p, 0);
        var normal = TvUi.Round(ctx, TvUi.Surface, 6, TvUi.Stroke, 1);
        var focus = TvUi.Round(ctx, TvUi.Surface2, 6, AColor.White, 2);
        _box.Background = normal;
        _box.FocusChange += (_, e) => _box.Background = e.HasFocus ? focus : normal;
        _box.TextChanged += (_, _) => Debounce();
        AddView(_box, TvUi.Lp(TvUi.Dp(ctx, 560), TvUi.Dp(ctx, 52), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 16)));

        _status = TvUi.Text_(ctx, 14, TvUi.TextFaint);
        AddView(_status, TvUi.Lp(-2, -2, TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 10)));

        AddView(TvGrid.Posters(ctx, _adapter), new LinearLayout.LayoutParams(-1, 0, 1f));
        _box.Post(() => _box.RequestFocus());
    }

    private void Debounce()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var q = _box.Text?.Trim() ?? "";
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(350, ct); } catch { return; }
            if (q.Length < 2) { Post(() => { _adapter.Replace(new()); _status.Text = ""; }); return; }
            await TvMedia.EnsureTokenAsync();
            var items = await _data.Get<TvData.MediaItem[]>($"/api/media?search={Uri.EscapeDataString(q)}&take=120", ct);
            if (ct.IsCancellationRequested) return;
            var cards = (items ?? Array.Empty<TvData.MediaItem>()).Select(i =>
            {
                var id = i.Id ?? ""; var title = i.Title; var fan = i.FanartPath;
                return new CardItem
                {
                    Kind = CardKind.Poster, Title = i.Title ?? "", Meta = TvData.PosterMeta(i),
                    ImageUrl = _data.Img(i.PosterPath ?? i.FanartPath, 330),
                    Click = () => _ = _page.Navigation.PushAsync(new TvDetailPage(id, title, fan)),
                };
            }).ToList();
            Post(() =>
            {
                _adapter.Replace(cards);
                _status.Text = cards.Count == 0 ? TvL.T("search.no_results", "Ничего не найдено", "Nothing found") : $"{cards.Count}";
            });
        });
    }
}
#endif
