#if ANDROID
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.Storage;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>Remote-control input facts the TV screens need.</summary>
internal static class TvInput
{
    /// <summary>Uptime (ms) of the last D-pad ARROW press. "Select on focus"
    /// controls (menu tabs, folder pills) only act when focus arrived from an
    /// arrow press — never from a programmatic / restored / default focus (e.g.
    /// after Back pops a page, Android may hand focus to the first tab).</summary>
    public static long LastArrowUptime;

    public static void OnKey(KeyEvent e)
    {
        if (e.Action != KeyEventActions.Down) return;
        if (e.KeyCode is Keycode.DpadLeft or Keycode.DpadRight or Keycode.DpadUp or Keycode.DpadDown)
            LastArrowUptime = SystemClock.UptimeMillis();
    }

    public static bool FocusFromArrow => SystemClock.UptimeMillis() - LastArrowUptime < 500;

    /// <summary>Wire "act when focus rests here after an arrow press" onto a view.</summary>
    public static void SelectOnFocus(AView v, Func<bool> enabled, Action act, int delayMs = 350)
    {
        int seq = 0;
        v.FocusChange += (_, e) =>
        {
            var s = ++seq;
            if (e.HasFocus && enabled() && FocusFromArrow)
                v.PostDelayed(() => { if (s == seq && v.HasFocus) act(); }, delayMs);
        };
    }
}

/// <summary>A selectable pill (folder filter, season): accent when active,
/// white when focused, surface otherwise. The active state changes in place —
/// no rebuild, so focus never jumps.</summary>
internal sealed class TvPill
{
    public TextView View { get; }
    private bool _active;
    public bool Active
    {
        get => _active;
        set { _active = value; Paint(); }
    }

    public TvPill(Android.Content.Context ctx, string text, bool active)
    {
        _active = active;
        View = TvUi.Text_(ctx, 15, TvUi.TextDim, TvUi.Medium, 1, text);
        View.Gravity = GravityFlags.Center;
        int hp = TvUi.Dp(ctx, 18);
        View.SetPadding(hp, 0, hp, 0);
        View.Focusable = true; View.Clickable = true; View.FocusableInTouchMode = false;
        View.FocusChange += (_, _) => Paint();
        Paint();
    }

    private void Paint()
    {
        var ctx = View.Context!;
        if (View.HasFocus)
        {
            View.Background = TvUi.Round(ctx, AColor.White, 20);
            View.SetTextColor(AColor.ParseColor("#1a1a1a"));
        }
        else
        {
            View.Background = TvUi.Round(ctx, _active ? TvUi.Accent : TvUi.Surface2, 20);
            View.SetTextColor(_active ? AColor.White : TvUi.TextDim);
        }
    }
}

/// <summary>TV-only preferences kept on the device.</summary>
internal static class TvPrefs
{
    private const string SectionOrderKey = "animarr_tv_section_order";

    /// <summary>User-chosen order of library sections on Home / Library.</summary>
    public static List<Guid> SectionOrder
    {
        get
        {
            try
            {
                return Preferences.Get(SectionOrderKey, "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty).ToList();
            }
            catch { return new(); }
        }
        set
        {
            try { Preferences.Set(SectionOrderKey, string.Join(',', value)); } catch { }
        }
    }

    /// <summary>Sort sections by the saved order; unknown ones keep label order at the end.</summary>
    public static List<(Guid Id, string Label)> Ordered(IEnumerable<(Guid Id, string Label)> sections)
    {
        var order = SectionOrder;
        return sections
            .OrderBy(s => { var i = order.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; })
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
#endif
