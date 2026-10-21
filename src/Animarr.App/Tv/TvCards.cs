#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Leanback.Widget;
using AndroidX.RecyclerView.Widget;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

internal enum CardKind { Poster, Wide, SeeAll, Episode }

/// <summary>One card on any TV rail / grid.</summary>
internal sealed class CardItem
{
    public CardKind Kind;
    public string Title = "";
    public string Meta = "";
    public string? ImageUrl;
    /// <summary>Small chip on the artwork (top-left): type, "Новое", air time, episode no.</summary>
    public string? Badge;
    public bool BadgeAccent;
    /// <summary>Top-right status chip (✓ watched etc.).</summary>
    public string? Corner;
    public AColor CornerColor = TvUi.Success;
    public double Progress;          // 0..1, >0.01 → bar on the art
    public bool ProgressDone;        // watched → full green bar
    public bool Dim;                 // missing file etc.
    public Action? Click;
    public Action? LongClick;
    public Action? Focused;
}

/// <summary>Card views + their recycling adapter. Each card = a fixed-size
/// vertical LinearLayout: artwork (ImageView with baked rounded corners, +
/// optional chips / progress bar) and 1–2 text lines underneath on the page
/// background — no overlay gradients, no clipping.</summary>
internal sealed class CardAdapter : RecyclerView.Adapter
{
    private List<CardItem> _items;
    public CardAdapter(List<CardItem> items) { _items = items; }
    public override int ItemCount => _items.Count;
    public override int GetItemViewType(int position) => (int)_items[position].Kind;

    public void Replace(List<CardItem> items) { _items = items; NotifyDataSetChanged(); }
    public CardItem? ItemAt(int pos) => pos >= 0 && pos < _items.Count ? _items[pos] : null;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        => CardHolder.Create(parent.Context!, (CardKind)viewType);

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        => ((CardHolder)holder).Bind(_items[position]);

    public override void OnViewRecycled(Java.Lang.Object holder)
    {
        if (holder is CardHolder h) TvImages.Clear(h.Image);
        base.OnViewRecycled(holder);
    }
}

internal sealed class CardHolder : RecyclerView.ViewHolder
{
    public readonly ImageView Image;
    private readonly TextView _title, _meta, _badge, _corner;
    private readonly FrameLayout _art;
    private readonly AView _barTrack, _barFill;
    private readonly CardKind _kind;
    private readonly int _wPx, _hPx, _radiusPx;
    private CardItem? _item;

    public static (int W, int H) ArtSize(CardKind k) => k switch
    {
        CardKind.Wide => (TvUi.WideW, TvUi.WideH),
        CardKind.Episode => (TvUi.EpW, TvUi.EpH),
        _ => (TvUi.PosterW, TvUi.PosterH),
    };

    /// <summary>Total card height in dp (art + text) — rails size themselves from it.</summary>
    public static int CardHeightDp(CardKind k) => ArtSize(k).H + 8 + 18 + 4 + 16;

    private CardHolder(AView root, CardKind kind, FrameLayout art, ImageView img, TextView title,
                       TextView meta, TextView badge, TextView corner, AView track, AView fill,
                       int wPx, int hPx, int rPx) : base(root)
    {
        _kind = kind; _art = art; Image = img; _title = title; _meta = meta; _badge = badge;
        _corner = corner; _barTrack = track; _barFill = fill; _wPx = wPx; _hPx = hPx; _radiusPx = rPx;

        root.Focusable = true;
        root.FocusableInTouchMode = false;
        root.Clickable = true;
        root.LongClickable = true;
        root.FocusChange += (_, e) =>
        {
            TvUi.CardFocus(root, art, title, e.HasFocus, 8);
            if (e.HasFocus) _item?.Focused?.Invoke();
        };
        root.Click += (_, _) => _item?.Click?.Invoke();
        root.LongClick += (_, e) =>
        {
            if (_item?.LongClick is { } lc) { lc(); e.Handled = true; }
            else e.Handled = false;
        };
    }

    public static CardHolder Create(Context ctx, CardKind kind)
    {
        var (wDp, hDp) = ArtSize(kind);
        int w = TvUi.Dp(ctx, wDp), h = TvUi.Dp(ctx, hDp), r = TvUi.Dp(ctx, 8);

        var root = TvUi.Column(ctx);
        root.LayoutParameters = new RecyclerView.LayoutParams(w, ViewGroup.LayoutParams.WrapContent);
        root.SetClipChildren(false);

        var art = new FrameLayout(ctx);
        root.AddView(art, new LinearLayout.LayoutParams(w, h));

        var img = new ImageView(ctx);
        img.SetScaleType(ImageView.ScaleType.CenterCrop);
        art.AddView(img, new FrameLayout.LayoutParams(-1, -1));

        int pad = TvUi.Dp(ctx, 8);
        var badge = TvUi.Text_(ctx, TvUi.TLabel, TvUi.Text, TvUi.Medium);
        badge.SetPadding(TvUi.Dp(ctx, 7), TvUi.Dp(ctx, 3), TvUi.Dp(ctx, 7), TvUi.Dp(ctx, 3));
        art.AddView(badge, new FrameLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.Top | GravityFlags.Start, LeftMargin = pad, TopMargin = pad });

        var corner = TvUi.Text_(ctx, TvUi.TLabel, TvUi.Text, TvUi.Semibold);
        corner.Gravity = GravityFlags.Center;
        art.AddView(corner, new FrameLayout.LayoutParams(TvUi.Dp(ctx, 24), TvUi.Dp(ctx, 24)) { Gravity = GravityFlags.Top | GravityFlags.End, RightMargin = pad, TopMargin = pad });

        // resume bar: inset from the rounded corners, so no clipping is needed
        var track = new AView(ctx) { Background = TvUi.Round(ctx, AColor.Argb(0x66, 0, 0, 0), 2) };
        art.AddView(track, new FrameLayout.LayoutParams(w - 2 * pad, TvUi.Dp(ctx, 4)) { Gravity = GravityFlags.Bottom | GravityFlags.Start, LeftMargin = pad, BottomMargin = pad });
        var fill = new AView(ctx);
        art.AddView(fill, new FrameLayout.LayoutParams(0, TvUi.Dp(ctx, 4)) { Gravity = GravityFlags.Bottom | GravityFlags.Start, LeftMargin = pad, BottomMargin = pad });

        TextView title, meta;
        if (kind == CardKind.SeeAll)
        {
            title = TvUi.Text_(ctx, TvUi.TTitle, TvUi.Text, TvUi.Medium);
            meta = TvUi.Text_(ctx, TvUi.TMeta, TvUi.TextFaint);
            var center = TvUi.Column(ctx);
            center.SetGravity(GravityFlags.Center);
            title.Gravity = GravityFlags.Center; meta.Gravity = GravityFlags.Center;
            center.AddView(title);
            center.AddView(meta, TvUi.Lp(-2, -2, 0, TvUi.Dp(ctx, 6)));
            art.Background = TvUi.Round(ctx, TvUi.Surface2, 8);
            art.AddView(center, new FrameLayout.LayoutParams(-1, -1));
        }
        else
        {
            title = TvUi.Text_(ctx, TvUi.TCardTitle, TvUi.TextDim, TvUi.Medium);
            meta = TvUi.Text_(ctx, TvUi.TLabel, TvUi.TextFaint);
            root.AddView(title, TvUi.Lp(-1, -2, TvUi.Dp(ctx, 2), TvUi.Dp(ctx, 8), TvUi.Dp(ctx, 2)));
            root.AddView(meta, TvUi.Lp(-1, -2, TvUi.Dp(ctx, 2), TvUi.Dp(ctx, 4), TvUi.Dp(ctx, 2)));
        }

        return new CardHolder(root, kind, art, img, title, meta, badge, corner, track, fill, w, h, r);
    }

    public void Bind(CardItem it)
    {
        _item = it;
        var ctx = ItemView.Context!;
        _title.Text = it.Title;
        _meta.Text = it.Meta;
        _meta.Visibility = string.IsNullOrEmpty(it.Meta) ? ViewStates.Gone : ViewStates.Visible;
        ItemView.Alpha = it.Dim ? 0.55f : 1f;

        if (_kind != CardKind.SeeAll)
            TvImages.Card(Image, it.ImageUrl, _wPx, _hPx, _radiusPx);

        if (!string.IsNullOrEmpty(it.Badge))
        {
            _badge.Text = it.Badge;
            _badge.Background = TvUi.Round(ctx, it.BadgeAccent ? TvUi.Accent : AColor.Argb(0xCC, 0x1c, 0x1c, 0x1c), 4);
            _badge.Visibility = ViewStates.Visible;
        }
        else _badge.Visibility = ViewStates.Gone;

        if (!string.IsNullOrEmpty(it.Corner))
        {
            _corner.Text = it.Corner;
            _corner.SetTextColor(AColor.ParseColor("#101010"));
            _corner.Background = TvUi.Round(ctx, it.CornerColor, 12);
            _corner.Visibility = ViewStates.Visible;
        }
        else _corner.Visibility = ViewStates.Gone;

        bool bar = it.ProgressDone || it.Progress > 0.01;
        _barTrack.Visibility = _barFill.Visibility = bar ? ViewStates.Visible : ViewStates.Gone;
        if (bar)
        {
            var full = _barTrack.LayoutParameters!.Width;
            var lp = _barFill.LayoutParameters!;
            lp.Width = (int)(full * (it.ProgressDone ? 1 : Math.Clamp(it.Progress, 0, 1)));
            _barFill.LayoutParameters = lp;
            _barFill.Background = TvUi.Round(ctx, it.ProgressDone ? TvUi.Success : TvUi.AccentHi, 2);
        }
    }

    /// <summary>Re-bind after an in-place model change (watched toggle).</summary>
    public void Refresh() { if (_item is not null) Bind(_item); }
}

/// <summary>Factory for a horizontal rail: Leanback HorizontalGridView keeps the
/// focused card at the left key line and remembers the last focused card when
/// focus comes back from another row.</summary>
internal static class Rails
{
    public static readonly RecyclerView.RecycledViewPool SharedPool = new();

    public static HorizontalGridView Horizontal(Context ctx, CardAdapter adapter, CardKind kind)
    {
        var g = new HorizontalGridView(ctx);
        int pv = TvUi.Dp(ctx, 12);
        g.SetPadding(TvUi.Dp(ctx, TvUi.SafeX), pv, TvUi.Dp(ctx, TvUi.SafeX), pv);
        g.SetClipToPadding(false);
        g.SetClipChildren(false);
        g.HorizontalSpacing = TvUi.Dp(ctx, TvUi.Gap);
        g.WindowAlignment = BaseGridView.WindowAlignBothEdge;
        g.WindowAlignmentOffset = TvUi.Dp(ctx, TvUi.SafeX);
        g.WindowAlignmentOffsetPercent = BaseGridView.WindowAlignOffsetPercentDisabled;
        g.ItemAlignmentOffsetPercent = 0;
        g.SetRecycledViewPool(SharedPool);
        g.HasFixedSize = true;
        g.SetAdapter(adapter);
        g.LayoutParameters = new ViewGroup.LayoutParams(-1, TvUi.Dp(ctx, CardHolder.CardHeightDp(kind)) + 2 * pv);
        return g;
    }
}
#endif
