#if ANDROID
using System.Net.Http.Json;
using System.Text.Json;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using AColor = Android.Graphics.Color;

namespace Animarr.App.Bench;

/// <summary>THROWAWAY prototype (tv-ui-analysis branch): the full-library grid
/// as ONE native RecyclerView behind a custom MAUI handler — recycled native
/// card views, Glide straight into ImageView, rounded corners via outline
/// clipping. Same data + same card look as LibraryAllPage/TvCards, so the two
/// can be profiled side by side.</summary>
public sealed class NativePosterGrid : Microsoft.Maui.Controls.View
{
    public List<PosterRow> Items { get; } = new();
}

public sealed record PosterRow(string Title, string Meta, string TypeLabel, string ImageUrl);

public sealed class NativePosterGridHandler : ViewHandler<NativePosterGrid, RecyclerView>
{
    public NativePosterGridHandler() : base(ViewHandler.ViewMapper) { }

    protected override RecyclerView CreatePlatformView()
    {
        var ctx = Context!;
        var d = ctx.Resources!.DisplayMetrics!.Density;
        var rv = new RecyclerView(ctx);
        rv.SetLayoutManager(new GridLayoutManager(ctx, 7));
        rv.SetAdapter(new PosterAdapter(VirtualView.Items));
        rv.SetItemViewCacheSize(14);
        rv.HasFixedSize = true;
        rv.SetClipToPadding(false);
        rv.SetClipChildren(false);
        rv.SetPadding((int)(24 * d), (int)(12 * d), (int)(14 * d), (int)(60 * d));
        rv.DescendantFocusability = Android.Views.DescendantFocusability.AfterDescendants;
        return rv;
    }
}

internal sealed class PosterAdapter : RecyclerView.Adapter
{
    private readonly List<PosterRow> _items;
    public PosterAdapter(List<PosterRow> items) { _items = items; HasStableIds = false; }
    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(Android.Views.ViewGroup parent, int viewType)
    {
        var ctx = parent.Context!;
        var d = ctx.Resources!.DisplayMetrics!.Density;
        int w = (int)(TvCards.PosterWidth * d), h = (int)(TvCards.PosterHeight * d);

        var card = new FrameLayout(ctx)
        {
            LayoutParameters = new RecyclerView.LayoutParams(w, h)
            {
                RightMargin = (int)(10 * d), BottomMargin = (int)(10 * d),
            },
            Focusable = true, Clickable = true,
        };
        card.SetBackgroundColor(AColor.ParseColor("#15171d"));
        var flat = BenchLog.Base == "rvflat";
        if (!flat) { card.OutlineProvider = new RoundOutline(10 * d); card.ClipToOutline = true; }

        var img = new ImageView(ctx) { };
        img.SetScaleType(ImageView.ScaleType.CenterCrop);
        // the web's saturate/brightness dim, as a colour filter: no extra layer
        img.SetColorFilter(AColor.Argb(0x21, 0, 0, 0), PorterDuff.Mode.SrcAtop!);
        card.AddView(img, new FrameLayout.LayoutParams(-1, -1));

        var shade = new AView(ctx)
        {
            Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!,
                new int[] { 0, 0, AColor.Argb(0xD9, 0x0A, 0x08, 0x07).ToArgb() }),
        };
        if (!flat) card.AddView(shade, new FrameLayout.LayoutParams(-1, -1));

        var badgeBg = new GradientDrawable();
        badgeBg.SetCornerRadius(3 * d);
        badgeBg.SetColor(AColor.Argb(0x99, 0, 0, 0));
        badgeBg.SetStroke((int)(1 * d), AColor.Argb(0x1F, 0xFF, 0xFF, 0xFF));
        var badge = new TextView(ctx) { Background = badgeBg, LetterSpacing = 0.14f };
        badge.SetTextColor(AColor.White);
        badge.SetTextSize(Android.Util.ComplexUnitType.Dip, 8);
        badge.SetPadding((int)(6 * d), (int)(2 * d), (int)(6 * d), (int)(2 * d));
        card.AddView(badge, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Start,
            LeftMargin = (int)(8 * d), TopMargin = (int)(8 * d),
        });

        var col = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        col.SetPadding((int)(8 * d), 0, (int)(8 * d), (int)(8 * d));
        var title = new TextView(ctx) { };
        title.SetTextColor(AColor.White);
        title.SetTextSize(Android.Util.ComplexUnitType.Dip, 10);
        title.SetTypeface(null, TypefaceStyle.Bold);
        title.SetMaxLines(2);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        var meta = new TextView(ctx) { };
        meta.SetTextColor(AColor.ParseColor("#8a91a0"));
        meta.SetTextSize(Android.Util.ComplexUnitType.Dip, 7.5f);
        meta.SetMaxLines(1);
        meta.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        col.AddView(title);
        col.AddView(meta);
        card.AddView(col, new FrameLayout.LayoutParams(-1, -2)
        {
            Gravity = Android.Views.GravityFlags.Bottom,
        });

        card.FocusChange += (s, e) => TvFocus.ApplyFocusVisual((AView)s!, e.HasFocus, 10f);
        return new Holder(card, img, badge, title, meta);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var h = (Holder)holder;
        var it = _items[position];
        h.Title.Text = it.Title;
        h.Meta.Text = it.Meta;
        h.Badge.Text = it.TypeLabel;
        h.Badge.Visibility = string.IsNullOrEmpty(it.TypeLabel) ? Android.Views.ViewStates.Gone : Android.Views.ViewStates.Visible;
        Bumptech.Glide.Glide.With(h.Image).Load(it.ImageUrl).Into(h.Image);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        public readonly ImageView Image; public readonly TextView Badge, Title, Meta;
        public Holder(AView v, ImageView img, TextView badge, TextView title, TextView meta) : base(v)
        { Image = img; Badge = badge; Title = title; Meta = meta; }
    }

    private sealed class RoundOutline : Android.Views.ViewOutlineProvider
    {
        private readonly float _r;
        public RoundOutline(float r) => _r = r;
        public override void GetOutline(AView? view, Outline? outline)
            => outline?.SetRoundRect(0, 0, view!.Width, view.Height, _r);
    }
}

/// <summary>Bench page: loads /api/media like LibraryAllPage, hosts the grid.</summary>
public sealed class NativeLibraryBenchPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NativePosterGrid _grid = new();

    public NativeLibraryBenchPage()
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0a0c10");
        BenchLog.Mark("rv-page-ctor");
        _ = LoadAsync();
    }

    private sealed record ApiItem(string? Id, string? Title, string? PosterPath, string? FanartPath,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount);

    private async Task LoadAsync()
    {
        var services = IPlatformApplication.Current!.Services;
        var http = services.GetRequiredService<HttpClient>();
        var addr = services.GetRequiredService<ServerAddressProvider>();
        var b = addr.Current!.ToString().TrimEnd('/');
        await TvMedia.EnsureTokenAsync();
        var items = await http.GetFromJsonAsync<ApiItem[]>("/api/media?take=1000", Json) ?? Array.Empty<ApiItem>();
        BenchLog.Mark($"rv-data n={items.Length}");
        foreach (var i in items.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                               .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            if (i.Year is > 0) parts.Add(i.Year.Value.ToString());
            if (i.EpisodeCount is > 0) parts.Add($"{i.EpisodeCount} EP");
            if (i.Rating is > 0) parts.Add($"★ {i.Rating:F1}");
            _grid.Items.Add(new PosterRow((i.Title ?? "").ToUpperInvariant(), string.Join(" · ", parts),
                i.MediaType switch { "Anime" => "ANIME", "Movie" => "MOVIE", "Series" => "SERIES", "Multserials" => "MULTI", _ => "" },
                TvMedia.Url($"{b}/api/image?path={Uri.EscapeDataString(i.PosterPath ?? i.FanartPath!)}&w=330")));
        }
        Content = _grid;
        BenchLog.MarkWithFrames($"rv-built n={_grid.Items.Count}");
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () =>
        {
            if (_grid.Handler?.PlatformView is RecyclerView rv) rv.GetChildAt(0)?.RequestFocus();
        });
    }
}
#endif
