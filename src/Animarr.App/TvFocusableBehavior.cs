#if ANDROID
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Animarr.App;

/// <summary>
/// Makes a MAUI view a natively-focusable Android view so the platform's D-pad
/// focus engine moves between catalog cards (no JS spatial nav). Drives the
/// focus highlight directly off the native FocusChange event — more reliable
/// than a VisualStateManager "Focused" state, which doesn't always fire for a
/// non-control container like Border. Android's RecyclerView auto-scrolls to
/// keep the focused card on screen.
/// </summary>
public sealed class TvFocusableBehavior : PlatformBehavior<View, Android.Views.View>
{
    private static readonly Color Ring = Color.FromArgb("#84c0d2");
    private EventHandler<Android.Views.View.FocusChangeEventArgs>? _handler;

    protected override void OnAttachedTo(View bindable, Android.Views.View platformView)
    {
        platformView.Focusable = true;
        platformView.FocusableInTouchMode = false;

        _handler = (_, e) =>
        {
            Android.Util.Log.Info("AnimarrFocus", $"FocusChange hasFocus={e.HasFocus} on {bindable.GetType().Name}");
            bindable.Scale  = e.HasFocus ? 1.10 : 1.0;
            bindable.ZIndex = e.HasFocus ? 10 : 0;
            if (bindable is Border b)
            {
                b.Stroke          = e.HasFocus ? Ring : Colors.Transparent;
                b.StrokeThickness = 4;
            }
        };
        platformView.FocusChange += _handler;
    }

    protected override void OnDetachedFrom(View bindable, Android.Views.View platformView)
    {
        if (_handler is not null) platformView.FocusChange -= _handler;
        _handler = null;
    }
}
#else
using Microsoft.Maui.Controls;

namespace Animarr.App;

public sealed class TvFocusableBehavior : Behavior<View> { }
#endif
