#if ANDROID
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Leanback.Widget;
using AndroidX.RecyclerView.Widget;
using Animarr.Shared.Models;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>Native TV title page: backdrop with a baked scrim, title / meta /
/// synopsis, Play + Favourite, season pills, then the episodes as rows of 4
/// cards in one virtualised VerticalGridView. Long-press an episode = mark
/// watched / unwatched. No on-screen Back (the remote's Back key does it).</summary>
public sealed class TvDetailPage : ContentPage
{
    private TvDetailView? _view;
    private bool _shown;

    public TvDetailPage(string id, string? title, string? fanartPath)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#1c1c1c");
        Content = new NativeHost(ctx => _view = new TvDetailView(ctx, this, id, title, fanartPath));
    }

    private bool _leaving;
    protected override bool OnBackButtonPressed()
    {
        if (_leaving) return true;            // 2nd Back during the pop animation
        _leaving = true;
        return base.OnBackButtonPressed();
    }

    protected override void OnDisappearing() { base.OnDisappearing(); _view?.Freeze(); }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _leaving = false;
        if (_shown) _view?.RefreshWatch();   // back from the player: progress moved
        _shown = true;
    }
}

internal sealed class TvDetailView : FrameLayout
{
    private readonly ContentPage _page;
    private readonly TvData _data = new();
    private readonly string _id;
    private readonly Guid _guid;
    private readonly VerticalGridView _rows;
    private readonly RowsAdapter _adapter;
    private readonly ImageView _backdrop;

    private TvData.Detail? _item;
    private TvData.FileRow[] _files = Array.Empty<TvData.FileRow>();
    private WatchStateDto[] _watch = Array.Empty<WatchStateDto>();
    private ContinueWatchDto? _continue;
    private bool _isFav;
    private int _season = 1;
    private List<int> _seasons = new();
    private List<CardItem> _episodes = new();
    private readonly string? _initialTitle;
    private HeaderHolder? _header;
    private readonly FocusKeeper _focus;

    public TvDetailView(Context ctx, ContentPage page, string id, string? title, string? fanartPath) : base(ctx)
    {
        _page = page; _id = id; Guid.TryParse(id, out _guid); _initialTitle = title;
        SetBackgroundColor(TvUi.Bg);

        _backdrop = new ImageView(ctx);
        _backdrop.SetScaleType(ImageView.ScaleType.CenterCrop);
        AddView(_backdrop, new LayoutParams(TvUi.Dp(ctx, 760), TvUi.Dp(ctx, 428)) { Gravity = GravityFlags.Top | GravityFlags.End });
        if (!string.IsNullOrEmpty(fanartPath)) TvImages.Backdrop(_backdrop, _data.Img(fanartPath, 1280), 960, 540);

        _rows = new VerticalGridView(ctx);
        _rows.SetClipToPadding(false);
        _rows.SetClipChildren(false);
        _rows.SetPadding(0, 0, 0, TvUi.Dp(ctx, 40));
        _rows.WindowAlignment = BaseGridView.WindowAlignBothEdge;
        _rows.WindowAlignmentOffsetPercent = 40f;
        _rows.ItemAlignmentOffsetPercent = 0;
        _rows.SetItemAnimator(null);
        _adapter = new RowsAdapter(this);
        _rows.SetAdapter(_adapter);
        AddView(_rows, new LayoutParams(-1, -1));
        _focus = new FocusKeeper(this);

        // the backdrop scrolls away with the header (no fixed full-screen layer)
        _rows.AddOnScrollListener(new ScrollFollower(_backdrop));
        _ = LoadAsync();
    }

    private sealed class ScrollFollower : RecyclerView.OnScrollListener
    {
        private readonly AView _v; private int _y;
        public ScrollFollower(AView v) => _v = v;
        public override void OnScrolled(RecyclerView rv, int dx, int dy)
        {
            _y += dy;
            _v.TranslationY = -_y;
            _v.Visibility = _y > _v.Height ? ViewStates.Invisible : ViewStates.Visible;
        }
    }

    // ── data ─────────────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        try
        {
            var token = TvMedia.EnsureTokenAsync();
            var itemT = _data.Get<TvData.Detail>($"/api/media/{_id}");
            var filesT = _data.Get<TvData.FileRow[]>($"/api/media/{_id}/files");
            Task<ContinueWatchDto?> contT = _guid != Guid.Empty ? TvData.Safe(_data.Api.GetContinueAsync(_guid)) : Task.FromResult<ContinueWatchDto?>(null);
            Task<WatchStateDto[]?> watchT = _guid != Guid.Empty ? TvData.Safe(_data.Api.GetWatchStatesAsync(_guid)) : Task.FromResult<WatchStateDto[]?>(null);
            Task<Guid[]?> favT = TvData.Safe(_data.Api.GetFavoriteIdsAsync());
            await token;
            _item = await itemT;
            _files = await filesT ?? Array.Empty<TvData.FileRow>();
            if (_item is null) return;
            TvData.Remember(_item.PosterPath, _item.FanartPath, _item.LastMetadataRefreshedAt);
            _continue = await contT;
            _watch = await watchT ?? Array.Empty<WatchStateDto>();
            _isFav = (await favT)?.Contains(_guid) ?? false;

            if (!string.IsNullOrEmpty(_item.FanartPath))
                TvImages.Backdrop(_backdrop, _data.Img(_item.FanartPath, 1280), 960, 540);

            ComputeSeasons();
            _season = _continue?.Season ?? (_seasons.Count > 0 ? _seasons[0] : 1);
            if (!_seasons.Contains(_season) && _seasons.Count > 0) _season = _seasons[0];
            BuildEpisodes();
            _adapter.NotifyDataSetChanged();
            Post(() => _header?.FocusPlay());
        }
        catch (Exception ex) { Android.Util.Log.Warn("AnimarrTv", $"detail load: {ex.Message}"); }
    }

    public void Freeze() => _focus.Freeze();

    public void RefreshWatch()
    {
        _focus.Restore(() => _header?.FocusPlay());
        _ = RefreshWatchAsync();
    }

    private async Task RefreshWatchAsync()
    {
        if (_guid == Guid.Empty || _item is null) return;
        _continue = await TvData.Safe(_data.Api.GetContinueAsync(_guid));
        _watch = await TvData.Safe(_data.Api.GetWatchStatesAsync(_guid)) ?? _watch;
        BuildEpisodes();
        _header?.Bind();
        _adapter.NotifyItemRangeChanged(1, _adapter.ItemCount - 1);
    }

    private bool IsMovie => _item?.MediaType == "Movie" || (_files.Length == 1 && _files[0].Episode is null);

    private void ComputeSeasons()
    {
        var set = new SortedSet<int>();
        if (_item?.Seasons is { Length: > 0 } ss) foreach (var s in ss) set.Add(s.Number);
        foreach (var f in _files) if (f.Season is not null || f.Episode is not null) set.Add(f.Season ?? 1);
        _seasons = set.ToList();
    }

    private void BuildEpisodes()
    {
        _episodes = new();
        if (_item is null || IsMovie) return;
        var meta = _item.Seasons?.FirstOrDefault(s => s.Number == _season);
        var maxFile = _files.Where(f => (f.Season ?? 1) == _season).Select(f => f.Episode ?? 0).DefaultIfEmpty(0).Max();
        var count = Math.Max(meta?.EpisodeCount ?? 0, maxFile);
        for (int i = 1; i <= count; i++)
        {
            var n = i;
            var f = _files.FirstOrDefault(x => (x.Season ?? 1) == _season && x.Episode == n);
            var ws = _watch.FirstOrDefault(w => (w.Season ?? 1) == _season && w.Episode == n);
            var have = f?.FilePath is { Length: > 0 };
            double frac = ws is { ProgressMs: > 0, RuntimeMs: > 0 } ? Math.Clamp((double)ws.ProgressMs!.Value / ws.RuntimeMs!.Value, 0, 1) : 0;
            var watched = ws?.IsWatched ?? false;
            var card = new CardItem
            {
                Kind = CardKind.Episode,
                Title = _season == 0 ? TvL.F("detail.special_fmt", "Спецвыпуск {0}", "Special {0}", n) : TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", n),
                Meta = !have ? TvL.T("episode_card.meta_missing", "Нет файла", "Not downloaded")
                     : watched ? TvL.T("detail.watched", "Просмотрено", "Watched")
                     : _item.Runtime is > 0 ? $"{_item.Runtime} мин" : "",
                ImageUrl = have ? _data.EpisodeThumb(_id, _season, n) : _data.Img(_item.FanartPath, 640),
                Badge = n.ToString("00"),
                Corner = watched ? "✓" : null,
                Progress = watched ? 0 : frac,
                ProgressDone = watched,
                Dim = !have,
            };
            var path = f?.FilePath;
            var resume = ws is { IsWatched: false } ? ws.ProgressMs ?? 0 : 0;
            if (have) card.Click = () => Play(_season, n, path, resume);
            card.LongClick = () => _ = ToggleWatchedAsync(n, path, card);
            _episodes.Add(card);
        }
    }

    // ── actions ──────────────────────────────────────────────────────────────
    private void Play(int? season, int? episode, string? path, long resumeMs)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = _page.Navigation.PushAsync(new PlayerPage(_guid, season, episode, path, _item?.Title ?? _initialTitle, resumeMs));
    }

    private void PlayCta()
    {
        if (_continue?.FilePath is { Length: > 0 } cf)
            Play(_continue.Season, _continue.Episode, cf, _continue.Kind == "continue" ? _continue.ProgressMs ?? 0 : 0);
        else
        {
            var f = _files.FirstOrDefault(x => !string.IsNullOrEmpty(x.FilePath));
            Play(f?.Season, f?.Episode, f?.FilePath, 0);
        }
    }

    private async Task ToggleFavAsync()
    {
        if (_guid == Guid.Empty) return;
        try
        {
            if (_isFav) await _data.Api.RemoveFavoriteAsync(_guid); else await _data.Api.AddFavoriteAsync(_guid);
            _isFav = !_isFav;
            _header?.Bind();
        }
        catch { }
    }

    private async Task ToggleWatchedAsync(int episode, string? path, CardItem card)
    {
        if (_guid == Guid.Empty) return;
        var watched = card.Corner is not null;
        var mark = watched ? TvL.T("movie_file_card.btn_mark_unwatched", "Отметить непросмотренным", "Mark as unwatched")
                           : TvL.T("movie_file_card.btn_mark_watched", "Отметить просмотренным", "Mark as watched");
        var choice = await _page.DisplayActionSheet(card.Title, TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, mark);
        if (choice != mark) return;
        try
        {
            await _data.Api.ToggleWatchedAsync(new Animarr.Shared.Requests.ToggleWatchedRequest(_guid, _season, episode, path, !watched));
            await RefreshWatchAsync();
        }
        catch { }
    }

    private void SwitchSeason(int s)
    {
        if (s == _season) return;
        _season = s;
        BuildEpisodes();
        _adapter.NotifyDataSetChanged();
    }

    // ── rows: 0 = header, 1 = season pills (if >1), then rows of 4 episodes ──
    private const int PerRow = 4;
    private bool HasSeasonRow => _seasons.Count > 1;
    private int EpisodeRowCount => (_episodes.Count + PerRow - 1) / PerRow;

    private sealed class RowsAdapter : RecyclerView.Adapter
    {
        private readonly TvDetailView _d;
        public RowsAdapter(TvDetailView d) { _d = d; }
        public override int ItemCount => _d._item is null ? 1 : 1 + (_d.HasSeasonRow ? 1 : 0) + _d.EpisodeRowCount;
        public override int GetItemViewType(int position)
            => position == 0 ? 0 : (_d.HasSeasonRow && position == 1 ? 1 : 2);

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) => viewType switch
        {
            0 => _d._header = new HeaderHolder(parent.Context!, _d),
            1 => new SeasonHolder(parent.Context!, _d),
            _ => new EpisodeRowHolder(parent.Context!),
        };

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            switch (holder)
            {
                case HeaderHolder h: _d._header = h; h.Bind(); break;
                case SeasonHolder s: s.Bind(); break;
                case EpisodeRowHolder e:
                    var row = position - 1 - (_d.HasSeasonRow ? 1 : 0);
                    e.Bind(_d._episodes.Skip(row * PerRow).Take(PerRow).ToList());
                    break;
            }
        }
    }

    private sealed class HeaderHolder : RecyclerView.ViewHolder
    {
        private readonly TvDetailView _d;
        private readonly TextView _title, _alt, _meta, _genres, _synopsis, _play, _fav;

        public HeaderHolder(Context ctx, TvDetailView d) : base(TvUi.Column(ctx))
        {
            _d = d;
            var col = (LinearLayout)ItemView;
            col.LayoutParameters = new RecyclerView.LayoutParams(-1, -2);
            col.SetClipChildren(false);
            col.SetClipToPadding(false);
            col.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, TvUi.SafeY), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 8));

            // visible Back (the remote's Back key does the same)
            var back = TvUi.Button(ctx, "‹  " + TvL.T("common.btn_back", "Назад", "Back"), false,
                () => _ = d._page.Navigation.PopAsync(), sp: 15);
            col.AddView(back, TvUi.Lp(-2, TvUi.Dp(ctx, 40), 0, 0, 0, TvUi.Dp(ctx, 28)));

            _title = TvUi.Text_(ctx, TvUi.TDisplay, TvUi.Text, TvUi.Semibold, 2, d._initialTitle);
            col.AddView(_title, TvUi.Lp(TvUi.Dp(ctx, 560), -2));
            _alt = TvUi.Text_(ctx, 15, TvUi.TextFaint);
            col.AddView(_alt, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 6)));
            _meta = TvUi.Text_(ctx, 15, TvUi.TextDim);
            col.AddView(_meta, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 12)));
            _genres = TvUi.Text_(ctx, 13, TvUi.AccentHi, TvUi.Medium);
            col.AddView(_genres, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 8)));
            _synopsis = TvUi.Text_(ctx, TvUi.TBody, TvUi.TextDim, TvUi.Regular, 3);
            _synopsis.SetLineSpacing(0, 1.25f);
            col.AddView(_synopsis, TvUi.Lp(TvUi.Dp(ctx, 560), -2, 0, TvUi.Dp(ctx, 14)));

            var buttons = TvUi.Row(ctx);
            buttons.SetClipChildren(false);
            _play = TvUi.Button(ctx, "▶  " + TvL.T("home.watch", "Смотреть", "Watch"), true, d.PlayCta);
            _fav = TvUi.Button(ctx, "", false, () => _ = d.ToggleFavAsync());
            buttons.AddView(_play, TvUi.Lp(-2, TvUi.Dp(ctx, 44)));
            buttons.AddView(_fav, TvUi.Lp(-2, TvUi.Dp(ctx, 44), TvUi.Dp(ctx, 12)));
            col.AddView(buttons, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 22)));
        }

        public void Bind()
        {
            var it = _d._item;
            if (it is null) return;
            _title.Text = it.Title ?? _d._initialTitle ?? "";
            var alt = it.EnglishTitle is { Length: > 0 } en && en != it.Title ? en : null;
            _alt.Text = alt ?? "";
            _alt.Visibility = alt is null ? ViewStates.Gone : ViewStates.Visible;
            var meta = new List<string>();
            if (it.Rating is > 0) meta.Add($"★ {it.Rating:0.0}");
            if (it.Year is not null) meta.Add(it.Year.ToString()!);
            var type = TvData.TypeLabel(it.MediaType); if (type.Length > 0) meta.Add(type);
            if (!string.IsNullOrEmpty(it.Studio)) meta.Add(it.Studio!);
            if (it.Runtime is > 0) meta.Add($"{it.Runtime} мин");
            if (it.EpisodeCount is > 0 && it.MediaType != "Movie") meta.Add($"{it.EpisodeCount} эп.");
            _meta.Text = string.Join("   ·   ", meta);
            _genres.Text = it.Genres is { Length: > 0 } g ? string.Join("  ·  ", g.Take(4)) : "";
            _genres.SetTextColor(TvUi.AccentHi);
            _synopsis.Text = string.IsNullOrEmpty(it.Description) ? TvL.T("detail.no_description", "Нет описания.", "No description.") : it.Description;

            var c = _d._continue;
            string ep(ContinueWatchDto x) => x.Episode is null ? "" : (x.Season is > 0 ? $" S{x.Season} · E{x.Episode}" : $" E{x.Episode}");
            _play.Text = c?.Kind switch
            {
                "continue" => "▶  " + TvL.T("continue.resume", "Продолжить", "Resume") + ep(c),
                "next" => "▶  " + TvL.T("detail.next", "Следующая", "Next") + ep(c),
                "rewatch" => "↺  " + TvL.T("detail.rewatch", "Пересмотреть", "Rewatch"),
                _ => "▶  " + TvL.T("home.watch", "Смотреть", "Watch"),
            };
            _fav.Text = _d._isFav ? "★  " + TvL.T("detail.in_favorites", "В избранном", "In favourites")
                                  : "☆  " + TvL.T("detail.add_favorite", "В избранное", "Add to favourites");
        }

        public void FocusPlay() => _play.RequestFocus();
    }

    private sealed class SeasonHolder : RecyclerView.ViewHolder
    {
        private readonly TvDetailView _d;
        private readonly LinearLayout _row;
        public SeasonHolder(Context ctx, TvDetailView d) : base(new HorizontalScrollView(ctx))
        {
            _d = d;
            var sv = (HorizontalScrollView)ItemView;
            sv.HorizontalScrollBarEnabled = false;
            sv.LayoutParameters = new RecyclerView.LayoutParams(-1, TvUi.Dp(ctx, 64));
            sv.SetClipChildren(false);
            _row = TvUi.Row(ctx);
            _row.SetClipChildren(false);
            _row.SetClipToPadding(false);
            _row.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 14), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 10));
            sv.AddView(_row, new ViewGroup.LayoutParams(-2, -1));
        }

        public void Bind()
        {
            var ctx = ItemView.Context!;
            _row.RemoveAllViews();
            foreach (var s in _d._seasons)
            {
                var n = s;
                var label = n == 0 ? TvL.T("media_detail.specials", "Спецвыпуски", "Specials")
                                   : TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", n);
                _row.AddView(TvGrid.Pill(ctx, label, n == _d._season, () => _d.SwitchSeason(n)), TvUi.Lp(-2, -1, 0, 0, TvUi.Dp(ctx, 10)));
            }
        }
    }

    /// <summary>One row of up to 4 episode cards (native focus moves between
    /// them; the outer grid scrolls row by row).</summary>
    private sealed class EpisodeRowHolder : RecyclerView.ViewHolder
    {
        private readonly CardHolder[] _cards = new CardHolder[PerRow];
        public EpisodeRowHolder(Context ctx) : base(TvUi.Row(ctx))
        {
            var row = (LinearLayout)ItemView;
            row.LayoutParameters = new RecyclerView.LayoutParams(-1, -2);
            row.SetClipChildren(false);
            row.SetClipToPadding(false);
            row.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 10), TvUi.Dp(ctx, TvUi.SafeX), TvUi.Dp(ctx, 6));
            for (int i = 0; i < PerRow; i++)
            {
                _cards[i] = CardHolder.Create(ctx, CardKind.Episode);
                row.AddView(_cards[i].ItemView, TvUi.Lp(TvUi.Dp(ctx, TvUi.EpW), -2, 0, 0, i < PerRow - 1 ? TvUi.Dp(ctx, TvUi.Gap) : 0));
            }
        }

        public void Bind(List<CardItem> items)
        {
            for (int i = 0; i < PerRow; i++)
            {
                if (i < items.Count) { _cards[i].ItemView.Visibility = ViewStates.Visible; _cards[i].Bind(items[i]); }
                else { _cards[i].ItemView.Visibility = ViewStates.Invisible; TvImages.Clear(_cards[i].Image); }
            }
        }
    }
}
#endif
