#if ANDROID
using Android.Graphics;
using Android.Widget;
using Bumptech.Glide;
using Bumptech.Glide.Load;
using Bumptech.Glide.Load.Engine.BitmapRecycle;
using Bumptech.Glide.Load.Model;
using Bumptech.Glide.Load.Resource.Bitmap;
using Bumptech.Glide.Request;

namespace Animarr.App.Tv;

/// <summary>
/// Glide straight into native ImageViews, tuned for a weak TV GPU:
///  • decode at the exact on-screen pixel size (Override) — and ask the server
///    for that width too (<c>?w=</c>), so neither side handles extra pixels;
///  • rounded corners / backdrop scrims BAKED into the bitmap once on a worker
///    thread — nothing is clipped or blended per frame;
///  • a cache key without <c>mt=</c>: the media token rotates every 6 h and
///    must not invalidate the whole disk cache.
/// </summary>
internal static class TvImages
{
    /// <summary>Poster / card art with baked rounded corners.</summary>
    public static void Card(ImageView iv, string? url, int wPx, int hPx, int radiusPx)
    {
        if (string.IsNullOrEmpty(url)) { Glide.With(iv).Clear(iv); iv.SetImageDrawable(null); return; }
        var opts = new RequestOptions()
            .Override(wPx, hPx)!
            .Transform(new MultiTransformation(new CenterCrop(), new RoundedCorners(radiusPx)))!;
        Glide.With(iv).Load(new StableUrl(url)).Apply((RequestOptions)opts).Into(iv);
    }

    /// <summary>Hero / detail backdrop with the page-colour scrim baked in
    /// (left + bottom fade), so the page needs no gradient layers.</summary>
    public static void Backdrop(ImageView iv, string? url, int wPx, int hPx)
    {
        if (string.IsNullOrEmpty(url)) { Glide.With(iv).Clear(iv); iv.SetImageDrawable(null); return; }
        var opts = new RequestOptions()
            .Override(wPx, hPx)!
            .Transform(ScrimTransform.Instance)!
            .DontAnimate()!;
        Glide.With(iv).Load(new StableUrl(url)).Apply((RequestOptions)opts).Into(iv);
    }

    public static void Clear(ImageView iv)
    {
        try { Glide.With(iv).Clear(iv); } catch { }
    }

    /// <summary>Server image URL sized for <paramref name="wPx"/> on-screen pixels.</summary>
    public static string ImageUrl(string serverBase, string path, int wPx)
        => TvMedia.Url($"{serverBase}/api/image?path={Uri.EscapeDataString(path)}&w={wPx}");

    /// <summary>GlideUrl whose cache key drops the rotating <c>mt=</c> token.</summary>
    private sealed class StableUrl : GlideUrl
    {
        private readonly string _key;
        public StableUrl(string url) : base(url) => _key = StripToken(url);
        public override string? CacheKey => _key;

        private static string StripToken(string url)
        {
            var i = url.IndexOf("mt=", StringComparison.Ordinal);
            if (i <= 0) return url;
            var end = url.IndexOf('&', i);
            var head = url[..(i - 1)];
            return end < 0 ? head : head + url[end..];
        }
    }

    /// <summary>Centre-crops and paints the page colour over the left 55 % and
    /// bottom 45 % of the backdrop. Runs once per image on a Glide worker.</summary>
    private sealed class ScrimTransform : BitmapTransformation
    {
        public static readonly ScrimTransform Instance = new();
        private static readonly byte[] Id = System.Text.Encoding.UTF8.GetBytes("animarr.tv.scrim.v2");

        protected override Bitmap? Transform(IBitmapPool pool, Bitmap toTransform, int outWidth, int outHeight)
        {
            var src = TransformationUtils.CenterCrop(pool, toTransform, outWidth, outHeight)!;
            var bmp = src.IsMutable ? src : src.Copy(Bitmap.Config.Argb8888!, true)!;
            using var c = new Canvas(bmp);
            int w = bmp.Width, h = bmp.Height;
            var bg = TvUi.Bg;
            using var p = new Android.Graphics.Paint();
            p.SetShader(new LinearGradient(0, 0, w * 0.62f, 0,
                new int[] { bg.ToArgb(), Android.Graphics.Color.Argb(0xB0, bg.R, bg.G, bg.B).ToArgb(), Android.Graphics.Color.Argb(0, bg.R, bg.G, bg.B).ToArgb() },
                new float[] { 0f, 0.45f, 1f }, Shader.TileMode.Clamp!));
            c.DrawRect(0, 0, w * 0.62f, h, p);
            // int[] overload on purpose: the (long, long) one takes colour LONGS
            // (with a colour-space id) and throws "Invalid ID" for ARGB ints.
            p.SetShader(new LinearGradient(0, h * 0.5f, 0, h,
                new int[] { Android.Graphics.Color.Argb(0, bg.R, bg.G, bg.B).ToArgb(), bg.ToArgb() },
                null, Shader.TileMode.Clamp!));
            c.DrawRect(0, h * 0.5f, w, h, p);
            return bmp;
        }

        public override void UpdateDiskCacheKey(Java.Security.MessageDigest messageDigest)
            => messageDigest.Update(Id);
    }
}
#endif
