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
    private TvLibraryView? _view;
    private bool _leaving;
    public TvLibraryPage(Guid? section = null)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => _view = new TvLibraryView(ctx, this, section));
    }

    private bool _shown;
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _leaving = false;
        if (_shown) _view?.OnReturn();
        _shown = true;
    }

    protected override void OnDisappearing() { base.OnDisappearing(); _view?.Freeze(); }

    // Back → the top menu first, then home.
    protected override bool OnBackButtonPressed()
    {
        if (_leaving) return true;            // 2nd Back during the pop animation
        if (_view is { } v && !v.Bar.HasFocus) { v.Bar.Active.RequestFocus(); return true; }
        _leaving = true;
        return base.OnBackButtonPressed();
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
        // Row-by-row paging: the focused row's top sits on a key line 12 dp below
        // the grid's top edge, so the row above scrolls fully out of view instead
        // of being sliced under the pills; 12 dp is room for the 1.08 focus zoom.
        g.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 12), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 40));
        g.SetClipToPadding(false);
        g.SetClipChildren(false);
        g.WindowAlignment = BaseGridView.WindowAlignBothEdge;
        g.WindowAlignmentOffset = TvUi.Dp(ctx, 12);
        g.WindowAlignmentOffsetPercent = BaseGridView.WindowAlignOffsetPercentDisabled;
        g.ItemAlignmentOffsetPercent = 0;
        g.SetItemAnimator(null);
        // Leanback grids keep focus inside by default - UP from the first row
        // must reach the section pills and the top menu.
        // In leanback-grid 1.0 the switch lives on its GridLayoutManager (not on
        // the view) — reach it through Java reflection.
        try
        {
            var lm = g.GetLayoutManager()!;
            lm.Class.GetMethod("setFocusOutAllowed", Java.Lang.Boolean.Type, Java.Lang.Boolean.Type)?
                .Invoke(lm, Java.Lang.Boolean.True, Java.Lang.Boolean.False);
        }
        catch (Exception ex) { Android.Util.Log.Warn("AnimarrTv", $"focusOut: {ex.Message}"); }
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
    private readonly List<(Guid? Id, TvPill Pill)> _pillViews = new();
    private TvData.MediaItem[] _items = Array.Empty<TvData.MediaItem>();
    private List<(Guid Id, string Label)> _sections = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sectionFolders = new();
    private Guid? _active;
    private readonly FocusKeeper _focus;
    public TvTopBar Bar { get; }
    private readonly bool _focusTab;

    public TvLibraryView(Context ctx, ContentPage page, Guid? section) : base(ctx)
    {
        _page = page; _active = section;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(TvUi.Bg);
        // keep clipping ON here: the grid scrolls under the pills, not over them

        Bar = new TvTopBar(ctx, TvTab.Library, page);
        AddView(Bar.View, TvUi.Lp(-1, TvUi.Dp(ctx, 40), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY), TvUi.Dp(ctx, TvUi.SafeX)));

        // no page title: the active "Библиотека" tab already says where we are
        _pills = TvUi.Row(ctx);
        _pills.SetClipChildren(false);
        _pills.SetClipToPadding(false);
        AddView(_pills, TvUi.Lp(-1, TvUi.Dp(ctx, 40), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 26), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 16)));

        _grid = TvGrid.Posters(ctx, _adapter);
        AddView(_grid, new LinearLayout.LayoutParams(-1, 0, 1f));
        _focus = new FocusKeeper(this);
        _focusTab = TvTopBar.TakePending(TvTab.Library);
        if (_focusTab) Bar.Active.Post(() => Bar.Active.RequestFocus());
        _ = LoadAsync();
    }

    /// <summary>Back from a title: focus returns to the card it was opened from.</summary>
    public void OnReturn()
    {
        if (_orderVersion != TvPrefs.OrderVersion && _sections.Count > 0)
        {
            _sections = TvPrefs.Ordered(_sections);
            BuildPills();
            var i = _pillViews.FindIndex(p => p.Id == _active);
            _pills.GetChildAt(Math.Max(0, i))?.Post(() => _pills.GetChildAt(Math.Max(0, i))?.RequestFocus());
            return;
        }
        _focus.Restore(() => _grid.RequestFocus());
    }
    private int _orderVersion = TvPrefs.OrderVersion;
    public void Freeze() => _focus.Freeze();

    private async Task LoadAsync()
    {
        var token = TvMedia.EnsureTokenAsync();
        var itemsT = _data.Get<TvData.MediaItem[]>("/api/media?take=1000" + TvData.CardView);
        var secT = TvData.Safe(_data.Api.GetSectionFoldersAsync());
        var folT = TvData.Safe(_data.Api.GetFoldersAsync());
        await token;
        _items = TvData.Remember(await itemsT)
            .Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        var secs = await secT; var fols = await folT;
        if (secs is not null)
        {
            _sections = TvPrefs.Ordered(secs.Select(s => (s.Id, (string)s.Label)));
            foreach (var s in secs)
            {
                var ids = fols?.Where(f => f.ParentSectionId == s.Id).Select(f => f.Id).ToHashSet() ?? new();
                ids.Add(s.Id);
                _sectionFolders[s.Id] = ids;
            }
        }
        if (_active is Guid a && !_sectionFolders.ContainsKey(a)) _active = null;
        BuildPills();
        ApplyFilter();
        if (!_focusTab) _grid.Post(() => { _grid.SelectedPosition = 0; _grid.RequestFocus(); });
    }

    private void BuildPills()
    {
        var ctx = Context!;
        _orderVersion = TvPrefs.OrderVersion;
        _pills.RemoveAllViews();
        _pillViews.Clear();
        if (_sections.Count == 0) return;
        void Add(Guid? id, string label)
        {
            var p = new TvPill(ctx, label, _active == id);
            p.View.Click += (_, _) => Switch(id);
            // like the menu tabs: resting on a folder after an arrow press selects it
            TvInput.SelectOnFocus(p.View, () => _active != id, () => Switch(id));
            _pills.AddView(p.View, TvUi.Lp(-2, -1, 0, 0, TvUi.Dp(ctx, 10)));
            _pillViews.Add((id, p));
        }
        Add(null, TvL.T("catalog.tab_all", "Все", "All"));
        foreach (var (id, label) in _sections) Add(id, label);
    }

    private void ApplyFilter()
    {
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
        _adapter.Replace(cards);
        _grid.SelectedPosition = 0;
    }

    private void Switch(Guid? id)
    {
        if (_active == id) return;
        _active = id;
        foreach (var (pid, pill) in _pillViews) pill.Active = pid == id;   // in place: focus stays put
        ApplyFilter();
    }
}

/// <summary>Search: one text box (the TV keyboard opens on OK) + the same
/// virtualised poster grid. Debounced 350 ms.</summary>
public sealed class TvSearchPage : ContentPage
{
    private TvSearchView? _view;
    private bool _leaving;
    public TvSearchPage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => _view = new TvSearchView(ctx, this));
    }

    private bool _shown;
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _leaving = false;
        if (_shown) _view?.OnReturn();
        _shown = true;
    }

    protected override void OnDisappearing() { base.OnDisappearing(); _view?.Freeze(); }

    protected override bool OnBackButtonPressed()
    {
        if (_leaving) return true;            // 2nd Back during the pop animation
        if (_view is { } v && !v.Bar.HasFocus) { v.Bar.Active.RequestFocus(); return true; }
        _leaving = true;
        return base.OnBackButtonPressed();
    }
}

internal sealed class TvSearchView : LinearLayout
{
    private readonly ContentPage _page;
    private readonly TvData _data = new();
    private readonly CardAdapter _adapter = new(new());
    private readonly EditText _box;
    private readonly TextView _status;
    private readonly VerticalGridView _grid;
    private bool _pending, _focusResults;
    private CancellationTokenSource? _cts;
    public TvTopBar Bar { get; }
    private readonly FocusKeeper _focus;
    public void OnReturn() => _focus.Restore(() => _box.RequestFocus());
    public void Freeze() => _focus.Freeze();

    public TvSearchView(Context ctx, ContentPage page) : base(ctx)
    {
        _page = page;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(TvUi.Bg);

        Bar = new TvTopBar(ctx, TvTab.Search, page);
        AddView(Bar.View, TvUi.Lp(-1, TvUi.Dp(ctx, 40), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY), TvUi.Dp(ctx, TvUi.SafeX)));

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
        AddView(_box, TvUi.Lp(TvUi.Dp(ctx, 560), TvUi.Dp(ctx, 52), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 28)));

        _status = TvUi.Text_(ctx, 14, TvUi.TextFaint);
        AddView(_status, TvUi.Lp(-2, -2, TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 10)));

        _grid = TvGrid.Posters(ctx, _adapter);
        AddView(_grid, new LinearLayout.LayoutParams(-1, 0, 1f));

        // D-pad path menu tab ↔ box ↔ results, spelled out: the geometric search
        // skipped the box both ways, and the empty results grid (invisible)
        // must not take focus at all.
        Bar.Active.Id = GenerateViewId(); _box.Id = GenerateViewId(); _grid.Id = GenerateViewId();
        Bar.Active.NextFocusDownId = _box.Id;
        _box.NextFocusUpId = Bar.Active.Id;
        _box.NextFocusDownId = _grid.Id;
        _grid.NextFocusUpId = _box.Id;
        _grid.Focusable = false;
        // EditText spends an UP / DOWN on moving the cursor to the text's start /
        // end first — on a remote that reads as a dead press, so leave at once.
        _box.KeyPress += (_, e) =>
        {
            e.Handled = false;
            if (e.Event?.Action != KeyEventActions.Down) return;
            if (e.KeyCode == Keycode.DpadUp) e.Handled = Bar.Active.RequestFocus();
            else if (e.KeyCode == Keycode.DpadDown && _adapter.ItemCount > 0) { FocusResults(); e.Handled = true; }
        };
        // the keyboard's Search key (or Enter): close the keyboard and land on the first result
        _box.EditorAction += (_, e) =>
        {
            bool enter = e.ActionId == Android.Views.InputMethods.ImeAction.ImeNull
                         && e.Event?.KeyCode is Keycode.Enter or Keycode.NumpadEnter;
            if (e.ActionId != Android.Views.InputMethods.ImeAction.Search && !enter) { e.Handled = false; return; }
            e.Handled = true;
            if (enter && e.Event!.Action != KeyEventActions.Up) return;
            (ctx.GetSystemService(Context.InputMethodService) as Android.Views.InputMethods.InputMethodManager)?
                .HideSoftInputFromWindow(_box.WindowToken, 0);
            if (_pending) _focusResults = true;   // still searching → focus the results when they land
            else if (_adapter.ItemCount > 0) FocusResults();
        };
        _focus = new FocusKeeper(this);
        if (TvTopBar.TakePending(TvTab.Search)) Bar.Active.Post(() => Bar.Active.RequestFocus());
        else _box.Post(() => _box.RequestFocus());
    }

    private void Debounce()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var q = _box.Text?.Trim() ?? "";
        _pending = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(350, ct); } catch { return; }
            if (q.Length < 2)
            {
                Post(() => { _pending = _focusResults = false; _adapter.Replace(new()); _grid.Focusable = false; _status.Text = ""; });
                return;
            }
            await TvMedia.EnsureTokenAsync();
            var items = await _data.Get<TvData.MediaItem[]>($"/api/media?search={Uri.EscapeDataString(q)}&take=120{TvData.CardView}", ct);
            if (ct.IsCancellationRequested) return;
            var cards = TvData.Remember(items).Select(i =>
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
                if (ct.IsCancellationRequested) return;
                _pending = false;
                _adapter.Replace(cards);
                _grid.Focusable = cards.Count > 0;
                _status.Text = cards.Count == 0 ? TvL.T("search.no_results", "Ничего не найдено", "Nothing found") : $"{cards.Count}";
                if (_focusResults) { _focusResults = false; if (cards.Count > 0) FocusResults(); }
            });
        });
    }

    private void FocusResults() => _grid.Post(() => { _grid.SelectedPosition = 0; _grid.RequestFocus(); });
}
#endif
