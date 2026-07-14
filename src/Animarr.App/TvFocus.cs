#if ANDROID
using System.Linq;
using System.Windows.Input;
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

    /// <summary>What D-pad OK runs. Bindable so it can be set from an item
    /// binding (<c>Command="{Binding Open}"</c>) or programmatically in
    /// code-behind — both reliable, unlike a TapGestureRecognizer Command bound
    /// through <c>x:Reference</c> (the gesture isn't in the visual tree, so that
    /// binding silently resolves to null on TV).</summary>
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(TvFocusBehavior));
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(TvFocusBehavior));
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

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
        // Clickable so Android natively converts a D-pad OK / Enter on the
        // focused view into a Click — far more reliable than a KeyPress hook,
        // which doesn't fire on a non-control Border with a real TV remote.
        v.Clickable = true;
        v.FocusChange += OnFocus;
        v.Click += OnClick;
    }

    private void Unwire()
    {
        if (_native is null) return;
        _native.FocusChange -= OnFocus;
        _native.Click -= OnClick;
        _native = null;
    }

    private void OnFocus(object? sender, AView.FocusChangeEventArgs e)
    {
        if (sender is AView v) TvFocus.ApplyFocusVisual(v, e.HasFocus, Radius);
    }

    // D-pad OK / Enter on a focused clickable view lands here as a Click.
    private void OnClick(object? sender, EventArgs e) => Activate();

    private void Activate()
    {
        // Prefer the behavior's own Command (item binding / code-behind) — that's
        // the reliable path. Fall back to a TapGestureRecognizer Command for any
        // template still using one.
        if (Command is { } c && c.CanExecute(CommandParameter))
        {
            c.Execute(CommandParameter);
            return;
        }
        var tap = _view?.GestureRecognizers?.OfType<TapGestureRecognizer>().FirstOrDefault();
        if (tap?.Command is { } tc && tc.CanExecute(tap.CommandParameter))
        {
            tc.Execute(tap.CommandParameter);
            return;
        }
        global::Android.Util.Log.Warn("AnimarrFocus", $"Activate no-op: cmd={Command is not null} tap={tap is not null}");
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

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(System.Windows.Input.ICommand), typeof(TvFocusBehavior));
    public System.Windows.Input.ICommand? Command { get => (System.Windows.Input.ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(TvFocusBehavior));
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
}
#endif
