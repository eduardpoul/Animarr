#if ANDROID
using System.Linq;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;
using AView = Android.Views.View;

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

    // Shared focus visual: scale, sky-blue ring, elevation — used by both the
    // recycled cards and the standalone TvFocusBehavior controls so every
    // focusable surface highlights identically.
    internal static void ApplyFocusVisual(AView v, bool hasFocus, float cornerDp = 12f)
    {
        var d = v.Resources?.DisplayMetrics?.Density ?? 2f;

        v.Animate()?.ScaleX(hasFocus ? 1.10f : 1f)?.ScaleY(hasFocus ? 1.10f : 1f)?
            .SetDuration(120)?.Start();

        if (hasFocus)
        {
            var ring = new Android.Graphics.Drawables.GradientDrawable();
            ring.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
            ring.SetCornerRadius(cornerDp * d);
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

    private sealed class CardFocusListener : Java.Lang.Object,
        RecyclerView.IOnChildAttachStateChangeListener
    {
        public void OnChildViewAttachedToWindow(AView view)
        {
            view.Focusable = true;
            view.FocusableInTouchMode = false;
            view.FocusChange -= OnFocus;
            view.FocusChange += OnFocus;
        }

        public void OnChildViewDetachedFromWindow(AView view)
            => view.FocusChange -= OnFocus;

        private static void OnFocus(object? sender, AView.FocusChangeEventArgs e)
        {
            if (sender is AView v) ApplyFocusVisual(v, e.HasFocus);
        }
    }
}

/// <summary>
/// Makes a single MAUI control (Border button, chip, season tab…) a native
/// D-pad focus target: focusable, the shared focus ring on FocusChange, and —
/// crucially — DPAD_CENTER/OK runs the control's TapGestureRecognizer Command
/// (with its CommandParameter), so a remote can actually activate it. Touch
/// still flows through MAUI's gesture, so no double-fire. Drop
/// <c>&lt;local:TvFocusBehavior/&gt;</c> on any Border to make it remote-reachable.
/// </summary>
public sealed class TvFocusBehavior : Behavior<View>
{
    /// <summary>Corner radius (dp) of the focus ring, to match the control.</summary>
    public float Radius { get; set; } = 12f;

    private View? _view;
    private AView? _native;

    protected override void OnAttachedTo(View view)
    {
        base.OnAttachedTo(view);
        _view = view;
        view.HandlerChanged += OnHandlerChanged;
        if (view.Handler is not null) Wire();
    }

    protected override void OnDetachingFrom(View view)
    {
        view.HandlerChanged -= OnHandlerChanged;
        Unwire();
        _view = null;
        base.OnDetachingFrom(view);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        Unwire();
        Wire();
    }

    private void Wire()
    {
        if (_view?.Handler?.PlatformView is not AView v) return;
        _native = v;
        v.Focusable = true;
        v.FocusableInTouchMode = false;
        v.FocusChange += OnFocus;
        v.KeyPress += OnKey;
    }

    private void Unwire()
    {
        if (_native is null) return;
        _native.FocusChange -= OnFocus;
        _native.KeyPress -= OnKey;
        _native = null;
    }

    private void OnFocus(object? sender, AView.FocusChangeEventArgs e)
    {
        if (sender is AView v) TvFocus.ApplyFocusVisual(v, e.HasFocus, Radius);
    }

    // Fire once, on key-up, for the OK / center / enter family. Touch is left to
    // MAUI's own TapGestureRecognizer handler, so the command never double-runs.
    private void OnKey(object? sender, AView.KeyEventArgs e)
    {
        var ev = e.Event;
        if (ev is null || ev.Action != Android.Views.KeyEventActions.Up)
        {
            e.Handled = false;
            return;
        }

        if (e.KeyCode is Android.Views.Keycode.DpadCenter
                      or Android.Views.Keycode.Enter
                      or Android.Views.Keycode.NumpadEnter
                      or Android.Views.Keycode.ButtonA)
        {
            Activate();
            e.Handled = true;
        }
        else
        {
            e.Handled = false;
        }
    }

    private void Activate()
    {
        var tap = _view?.GestureRecognizers?.OfType<TapGestureRecognizer>().FirstOrDefault();
        if (tap?.Command is { } cmd && cmd.CanExecute(tap.CommandParameter))
            cmd.Execute(tap.CommandParameter);
    }
}
#else
using Microsoft.Maui.Controls;

namespace Animarr.App;

public static class TvFocus
{
    public static void Attach(CollectionView cv) { }
}

// No-op on non-Android targets so the XAML still compiles everywhere.
public sealed class TvFocusBehavior : Behavior<View>
{
    public float Radius { get; set; } = 12f;
}
#endif
