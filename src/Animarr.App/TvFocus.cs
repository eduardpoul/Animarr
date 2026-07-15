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
    internal static void ApplyFocusVisual(AView v, bool hasFocus, float cornerDp = 12f, bool fillBg = false)
    {
        var d = v.Resources?.DisplayMetrics?.Density ?? 2f;

        // Frameless fill buttons (fillBg) do NOT zoom — the translucent pill
        // alone marks focus, matching the web player. Crucially it also keeps the
        // highlight inside fixed-width containers: an over-scaled row would spill
        // past the submenu panel and get clipped by its rounded border. Posters
        // (fillBg = false) zoom gently — 1.05 keeps the scaled card + ring inside
        // the 10dp grid gaps (1.10 visibly overlapped the neighbours).
        float scale = hasFocus && !fillBg ? 1.05f : 1f;
        v.Animate()?.ScaleX(scale)?.ScaleY(scale)?.SetDuration(120)?.Start();

        if (hasFocus)
        {
            if (fillBg)
            {
                // Web-style frameless button: the focused one fills with a
                // translucent white pill — no outline; the fill alone reads as
                // the selection (matches the browser Artplayer HUD).
                var fill = new Android.Graphics.Drawables.GradientDrawable();
                fill.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                fill.SetCornerRadius(cornerDp * d);
                fill.SetColor(Android.Graphics.Color.ParseColor("#59FFFFFF"));
                v.Foreground = fill;
            }
            else
            {
                // Web tv-mode focus (tokens.css html.tv-mode *:focus-visible):
                // accent ring + soft accent glow. MAUI's Border clips ALL of the
                // view's own drawing to its rounded outline (clipToOutline), so
                // an outside-the-bounds ring vanishes — instead the 3dp accent
                // ring hugs the edge with a 5dp soft glow bleeding inward.
                var ring = new Android.Graphics.Drawables.GradientDrawable();
                ring.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                ring.SetCornerRadius(cornerDp * d);
                ring.SetStroke((int)(3 * d), Android.Graphics.Color.ParseColor("#f5934f"));
                var glow = new Android.Graphics.Drawables.GradientDrawable();
                glow.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
                glow.SetCornerRadius((cornerDp - 2) * d);
                glow.SetStroke((int)(5 * d), Android.Graphics.Color.ParseColor("#40e8772e"));
                var layers = new Android.Graphics.Drawables.LayerDrawable(
                    new Android.Graphics.Drawables.Drawable[] { glow, ring });
                int glowIn = (int)(2 * d);
                layers.SetLayerInset(0, glowIn, glowIn, glowIn, glowIn);
                v.Foreground = layers;
            }
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

    /// <summary>Web-style: no background until focused, then a translucent pill
    /// fills behind the icon. Use for frameless icon buttons (the player HUD).</summary>
    public bool FillOnFocus { get; set; }

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

    /// <summary>Runs on a LONG press of D-pad OK (or a touch long-press) —
    /// the TV-remote counterpart of a context menu (e.g. mark episode watched).</summary>
    public static readonly BindableProperty LongCommandProperty =
        BindableProperty.Create(nameof(LongCommand), typeof(ICommand), typeof(TvFocusBehavior));
    public ICommand? LongCommand { get => (ICommand?)GetValue(LongCommandProperty); set => SetValue(LongCommandProperty, value); }

    private View? _view;
    private AView? _native;

    protected override void OnAttachedTo(View view)
    {
        base.OnAttachedTo(view);
        _view = view;
        // MAUI Behaviors do NOT inherit the host's BindingContext, so an XAML
        // Command="{Binding Open}" inside a DataTemplate resolves against null
        // and D-pad OK silently no-ops. Mirror the host's context (and follow
        // its changes) so item-bound commands actually resolve.
        BindingContext = view.BindingContext;
        view.BindingContextChanged += OnHostBindingContextChanged;
        view.HandlerChanged += OnHandlerChanged;
        if (view.Handler is not null) Wire();
    }

    protected override void OnDetachingFrom(View view)
    {
        view.BindingContextChanged -= OnHostBindingContextChanged;
        view.HandlerChanged -= OnHandlerChanged;
        Unwire();
        _view = null;
        base.OnDetachingFrom(view);
    }

    private void OnHostBindingContextChanged(object? sender, EventArgs e)
        => BindingContext = _view?.BindingContext;

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
        // LongClickable so a held D-pad OK / touch long-press raises LongClick;
        // the handler passes through (Handled=false) when no LongCommand is set,
        // so plain buttons keep behaving like a normal click.
        v.LongClickable = true;
        v.FocusChange += OnFocus;
        v.Click += OnClick;
        v.LongClick += OnLongClick;
    }

    private void Unwire()
    {
        if (_native is null) return;
        _native.FocusChange -= OnFocus;
        _native.Click -= OnClick;
        _native.LongClick -= OnLongClick;
        _native = null;
    }

    private void OnFocus(object? sender, AView.FocusChangeEventArgs e)
    {
        if (sender is AView v) TvFocus.ApplyFocusVisual(v, e.HasFocus, Radius, FillOnFocus);
    }

    // D-pad OK / Enter on a focused clickable view lands here as a Click.
    private void OnClick(object? sender, EventArgs e) => Activate();

    private void OnLongClick(object? sender, AView.LongClickEventArgs e)
    {
        if (LongCommand is { } lc && lc.CanExecute(CommandParameter))
        {
            lc.Execute(CommandParameter);
            e.Handled = true;
            return;
        }
        // No long-press action — let the platform fall back to a normal click.
        e.Handled = false;
    }

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
    public bool FillOnFocus { get; set; }

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(System.Windows.Input.ICommand), typeof(TvFocusBehavior));
    public System.Windows.Input.ICommand? Command { get => (System.Windows.Input.ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(TvFocusBehavior));
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    public static readonly BindableProperty LongCommandProperty =
        BindableProperty.Create(nameof(LongCommand), typeof(System.Windows.Input.ICommand), typeof(TvFocusBehavior));
    public System.Windows.Input.ICommand? LongCommand { get => (System.Windows.Input.ICommand?)GetValue(LongCommandProperty); set => SetValue(LongCommandProperty, value); }
}
#endif
