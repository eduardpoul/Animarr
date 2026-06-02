#if ANDROID
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;

namespace Animarr.App;

/// <summary>
/// Wires native Android-TV D-pad focus + a focus highlight onto a MAUI
/// CollectionView's underlying RecyclerView. MAUI doesn't expose per-item
/// focus, so we make each item view focusable and draw the ring + zoom +
/// elevation natively — what real Android-TV apps do (no tv-nav.js).
/// </summary>
public static class TvFocus
{
    public static void Attach(CollectionView cv)
    {
        cv.Loaded += (_, _) =>
        {
            if (cv.Handler?.PlatformView is RecyclerView rv)
            {
                rv.SetItemViewCacheSize(28);
                rv.SetClipChildren(false);     // let a focused card's scale-up overflow
                rv.SetClipToPadding(false);
                rv.AddOnChildAttachStateChangeListener(new CardFocusListener());
            }
        };
    }

    private sealed class CardFocusListener : Java.Lang.Object,
        RecyclerView.IOnChildAttachStateChangeListener
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
}
#else
using Microsoft.Maui.Controls;

namespace Animarr.App;

public static class TvFocus
{
    public static void Attach(CollectionView cv) { }
}
#endif
