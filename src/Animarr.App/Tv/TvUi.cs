#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

/// <summary>
/// The native TV design kit: Fluent 2 (Windows 11 dark) palette from the web's
/// tokens.css, 10-foot sizes on the 960×540 dp TV canvas, and the one focus
/// language every card / button uses. Everything here is plain Android Views —
/// no MAUI layout, no path clips, no stacked translucent layers — so a weak
/// TV GPU (Mali-G52 MC1) draws each card in one or two passes.
/// </summary>
internal static class TvUi
{
    // ── Canvas: 960×540 dp (Google TV layout grid: 12 × 52 dp columns, 20 dp
    //    gutters, 58 dp side margins; ≥5 % overscan-safe top/bottom = 28 dp). ──
    public const int SafeX = 58;
    public const int SafeY = 28;
    public const int Gap = 20;            // gutter between cards / rails

    // ── Card sizes (dp) = grid spans. Poster 2:3, landscape 16:9. ────────────
    public const int PosterW = 124, PosterH = 186;     // 2-column span → 6 per row
    public const int WideW = 268, WideH = 151;         // 4-column span → 3 per row (+ peek)
    public const int EpW = 196, EpH = 110;             // 3-column span → 4 per row
    public const int WideSmallW = 188, WideSmallH = 106; // "this week" — 70 % of Wide, 4.5 per row

    // ── Type scale (sp) — nothing under 12 sp at 10 feet. ────────────────────
    public const float TDisplay = 34, THeadline = 24, TSection = 20, TTitle = 16,
                       TBody = 16, TCardTitle = 14, TMeta = 13, TLabel = 12;

    // ── Fluent 2 dark (tokens.css :root) ─────────────────────────────────────
    public static readonly AColor Bg       = AColor.ParseColor("#1c1c1c");
    public static readonly AColor Surface  = AColor.ParseColor("#2b2b2b");
    public static readonly AColor Surface2 = AColor.ParseColor("#323232");
    public static readonly AColor Surface3 = AColor.ParseColor("#3a3a3a");
    public static readonly AColor Stroke   = AColor.ParseColor("#454545");
    public static readonly AColor Text     = AColor.White;
    public static readonly AColor TextDim  = AColor.ParseColor("#c8c8c8");
    public static readonly AColor TextFaint= AColor.ParseColor("#9a9a9a");
    public static readonly AColor Success  = AColor.ParseColor("#6ccb5f");   // Fluent success fg
    public static readonly AColor Caution  = AColor.ParseColor("#fce100");

    /// <summary>Accent = oklch(0.56 0.16 hue) / accent-hi = oklch(0.75 0.14 hue),
    /// hue from the user's AccentHue preference (web default 50).</summary>
    public static AColor Accent   { get; private set; } = Oklch(0.56, 0.16, SavedHue());
    public static AColor AccentHi { get; private set; } = Oklch(0.75, 0.14, SavedHue());

    private const string HueKey = "animarr_tv_accent_hue";
    private static int SavedHue()
    {
        try { return Microsoft.Maui.Storage.Preferences.Get(HueKey, 50); } catch { return 50; }
    }

    /// <summary>Apply + remember the profile's accent, so the next launch paints
    /// the right colour from the first frame (no default-orange flash).</summary>
    public static void SetAccentHue(int hue)
    {
        Accent = Oklch(0.56, 0.16, hue);
        AccentHi = Oklch(0.75, 0.14, hue);
        try { Microsoft.Maui.Storage.Preferences.Set(HueKey, hue); } catch { }
    }

    // ── Units ────────────────────────────────────────────────────────────────
    public static float Density(Context ctx) => ctx.Resources?.DisplayMetrics?.Density ?? 2f;
    public static int Dp(Context ctx, float dp) => (int)Math.Round(dp * Density(ctx));

    // ── Typefaces: the system sans (Roboto / Google Sans on Google TV) ───────
    public static readonly Typeface Regular  = Typeface.Create("sans-serif", TypefaceStyle.Normal)!;
    public static readonly Typeface Medium   = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal)!;
    public static readonly Typeface Semibold = Typeface.Create("sans-serif-medium", TypefaceStyle.Bold)!;

    public static TextView Text_(Context ctx, float sp, AColor color, Typeface? face = null,
                                 int maxLines = 1, string? text = null)
    {
        var t = new TextView(ctx) { Text = text ?? "" };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        t.Typeface = face ?? Regular;
        t.SetMaxLines(maxLines);
        t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        t.SetIncludeFontPadding(false);
        return t;
    }

    public static GradientDrawable Round(Context ctx, AColor fill, float radiusDp,
                                         AColor? stroke = null, float strokeDp = 0)
    {
        var g = new GradientDrawable();
        g.SetCornerRadius(Dp(ctx, radiusDp));
        g.SetColor(fill);
        if (stroke is AColor s && strokeDp > 0) g.SetStroke(Dp(ctx, strokeDp), s);
        return g;
    }

    public static LinearLayout Column(Context ctx) => new(ctx) { Orientation = Orientation.Vertical };
    public static LinearLayout Row(Context ctx) => new(ctx) { Orientation = Orientation.Horizontal };

    public static LinearLayout.LayoutParams Lp(int w, int h, int l = 0, int t = 0, int r = 0, int b = 0)
    {
        var lp = new LinearLayout.LayoutParams(w, h);
        lp.SetMargins(l, t, r, b);
        return lp;
    }

    // ── Focus language ───────────────────────────────────────────────────────
    // Cards: whole card scales 1.08 (RenderNode property — no relayout), a 3 dp
    // white ring hugs the artwork, the title brightens. No elevation shadow, no
    // BringToFront (requestLayout). Buttons: white fill + dark text when focused.

    public const float CardScale = 1.08f;
    public const int FocusAnimMs = 160;

    public static void CardFocus(AView card, AView art, TextView? title, bool focused, float radiusDp)
    {
        card.Animate()?.ScaleX(focused ? CardScale : 1f)?.ScaleY(focused ? CardScale : 1f)
            ?.SetDuration(FocusAnimMs)?.Start();
        art.Foreground = focused ? Round(art.Context!, AColor.Transparent, radiusDp, AColor.White, 3) : null;
        if (title is not null) title.SetTextColor(focused ? Text : TextDim);
        card.TranslationZ = focused ? 1f : 0f;   // draw over neighbours, no shadow (no elevation)
    }

    /// <summary>A pill / rounded button: primary = accent fill, secondary =
    /// surface fill; focused = white fill + near-black text (most legible at 10 ft).</summary>
    public static TextView Button(Context ctx, string text, bool primary, Action onClick, float sp = TTitle)
    {
        var b = Text_(ctx, sp, primary ? AColor.White : Text, Medium, 1, text);
        b.Gravity = GravityFlags.Center;
        int hp = Dp(ctx, 22), vp = Dp(ctx, 11);
        b.SetPadding(hp, vp, hp, vp);
        b.Focusable = true; b.Clickable = true; b.FocusableInTouchMode = false;
        var normalBg = Round(ctx, primary ? Accent : Surface2, 6);
        var focusBg = Round(ctx, AColor.White, 6);
        b.Background = normalBg;
        b.FocusChange += (_, e) =>
        {
            b.Background = e.HasFocus ? focusBg : normalBg;
            b.SetTextColor(e.HasFocus ? AColor.ParseColor("#1a1a1a") : (primary ? AColor.White : Text));
            b.Animate()?.ScaleX(e.HasFocus ? 1.05f : 1f)?.ScaleY(e.HasFocus ? 1.05f : 1f)?.SetDuration(FocusAnimMs)?.Start();
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Re-colour a <see cref="Button"/> (e.g. favourite on/off).</summary>
    public static void SetButtonText(TextView b, string text) => b.Text = text;

    public static AView Spacer(Context ctx, int wDp, int hDp)
        => new(ctx) { LayoutParameters = new ViewGroup.LayoutParams(Dp(ctx, wDp), Dp(ctx, hDp)) };

    /// <summary>Thin progress bar (track + fill) as two plain views.</summary>
    public static FrameLayout Progress(Context ctx, int widthPx, double fraction, AColor fill, int heightDp = 4)
    {
        var f = new FrameLayout(ctx);
        var track = new AView(ctx) { Background = Round(ctx, AColor.Argb(0x55, 0xff, 0xff, 0xff), heightDp / 2f) };
        f.AddView(track, new FrameLayout.LayoutParams(widthPx, Dp(ctx, heightDp)));
        var bar = new AView(ctx) { Background = Round(ctx, fill, heightDp / 2f) };
        f.AddView(bar, new FrameLayout.LayoutParams((int)(widthPx * Math.Clamp(fraction, 0, 1)), Dp(ctx, heightDp)));
        return f;
    }

    // ── OKLCH → sRGB (same maths the browser uses for the web accent) ────────
    public static AColor Oklch(double l, double c, double hDeg)
    {
        double h = hDeg * Math.PI / 180, a = c * Math.Cos(h), b = c * Math.Sin(h);
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        double L = l_ * l_ * l_, M = m_ * m_ * m_, S = s_ * s_ * s_;
        double r = +4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S;
        double g = -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S;
        double bl = -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S;
        static int Gam(double x)
        {
            x = Math.Clamp(x, 0, 1);
            x = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
            return (int)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }
        return AColor.Rgb(Gam(r), Gam(g), Gam(bl));
    }
}

/// <summary>Remembers the last focused view inside a native screen and puts
/// focus back there when the page re-appears (after the player / detail page),
/// so the remote never lands "nowhere" and Back keeps its meaning.</summary>
internal sealed class FocusKeeper : Java.Lang.Object, ViewTreeObserver.IOnGlobalFocusChangeListener
{
    private readonly ViewGroup _root;
    private WeakReference<AView>? _last;
    public FocusKeeper(ViewGroup root)
    {
        _root = root;
        root.ViewAttachedToWindow += (_, _) => root.ViewTreeObserver?.AddOnGlobalFocusChangeListener(this);
        root.ViewDetachedFromWindow += (_, _) => root.ViewTreeObserver?.RemoveOnGlobalFocusChangeListener(this);
    }

    private bool _frozen;

    /// <summary>Call when the page goes away: the focus Android hands out while
    /// it is hidden / coming back (often the first menu tab) must not overwrite
    /// the remembered card.</summary>
    public void Freeze() => _frozen = true;

    public void OnGlobalFocusChanged(AView? oldFocus, AView? newFocus)
    {
        if (newFocus is null || _frozen) return;
        for (var p = newFocus.Parent; p is not null; p = p.Parent)
            if (ReferenceEquals(p, _root)) { _last = new WeakReference<AView>(newFocus); return; }
    }

    public AView? Last => _last is not null && _last.TryGetTarget(out var v) ? v : null;

    /// <summary>Re-focus the remembered view (if still attached), else <paramref name="fallback"/>.</summary>
    public void Restore(Action? fallback = null) => _root.PostDelayed(() =>
    {
        _frozen = false;
        if (_last is not null && _last.TryGetTarget(out var v) && v.IsAttachedToWindow)
        {
            if (v.HasFocus || v.RequestFocus()) return;
        }
        fallback?.Invoke();
    }, 120);
}

/// <summary>Hosts one native Android view tree as a MAUI page's content. The
/// native view gets the page's full size (EXACTLY specs), so a RecyclerView
/// inside never "wraps content" and keeps virtualising.</summary>
public sealed class NativeHost : Microsoft.Maui.Controls.View
{
    public NativeHost(Func<Context, AView> factory) => Factory = factory;
    public Func<Context, AView> Factory { get; }
    public AView? Native { get; internal set; }
}

public sealed class NativeHostHandler : ViewHandler<NativeHost, AView>
{
    public NativeHostHandler() : base(ViewHandler.ViewMapper) { }

    protected override AView CreatePlatformView()
    {
        var v = VirtualView.Factory(Context!);
        VirtualView.Native = v;
        return v;
    }

    public override Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(double.IsInfinity(widthConstraint) ? 960 : widthConstraint,
               double.IsInfinity(heightConstraint) ? 540 : heightConstraint);

    // MAUI only calls Layout() on the platform view; without an EXACTLY measure
    // the native children keep a 0×0 measured size and nothing is drawn.
    public override void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
    {
        var d = Context!.Resources!.DisplayMetrics!.Density;
        int w = (int)Math.Round(frame.Width * d), h = (int)Math.Round(frame.Height * d);
        PlatformView.Measure(AView.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly),
                             AView.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
        base.PlatformArrange(frame);
    }
}
#endif
