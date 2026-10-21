#if ANDROID
using Android.Content;
using Android.Views;
using Android.Widget;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace Animarr.App.Tv;

internal enum TvTab { Home, Library, Search }

/// <summary>The one top menu every main TV screen shares (Google TV pattern):
/// logo, pill tabs Главная · Библиотека · Поиск, Профиль on the right. Switching
/// a tab keeps the stack at [Home, tab], so Back from Library / Search always
/// lands on Home — no separate on-screen Back button needed there.</summary>
internal sealed class TvTopBar
{
    /// <summary>Set when a tab switch was triggered by moving focus along the
    /// menu: the next screen then keeps focus on its own tab (so the user can
    /// keep walking the menu) instead of jumping into the content.</summary>
    public static TvTab? PendingFocus;

    public static bool TakePending(TvTab tab)
    {
        if (PendingFocus != tab) return false;
        PendingFocus = null;
        return true;
    }

    public LinearLayout View { get; }
    public TextView Active { get; }
    public bool HasFocus => View.HasFocus;

    /// <summary>Row that, when focus ENTERS it from above/below, offers only the
    /// current tab — so stepping up into the menu lands on "where I am", never
    /// on a neighbour tab (which would open that page on focus).</summary>
    private sealed class BarLayout : LinearLayout
    {
        public AView? Preferred;
        public BarLayout(Context ctx) : base(ctx) { Orientation = Orientation.Horizontal; }
        public override void AddFocusables(IList<AView>? views, [Android.Runtime.GeneratedEnum] FocusSearchDirection direction, [Android.Runtime.GeneratedEnum] FocusablesFlags focusableMode)
        {
            if (!HasFocus && Preferred is not null && views is not null &&
                (direction == FocusSearchDirection.Up || direction == FocusSearchDirection.Down))
            {
                views.Add(Preferred);
                return;
            }
            base.AddFocusables(views, direction, focusableMode);
        }
    }

    public TvTopBar(Context ctx, TvTab active, ContentPage page)
    {
        View = new BarLayout(ctx);
        View.SetGravity(GravityFlags.CenterVertical);
        View.SetClipChildren(false);
        View.SetClipToPadding(false);

        View.AddView(TvUi.Text_(ctx, 22, TvUi.Text, TvUi.Semibold, 1, "Animarr"), TvUi.Lp(-2, -2, 0, 0, TvUi.Dp(ctx, 28)));

        void Open(TvTab t)
        {
            if (t == active) return;
            PendingFocus = t;
            if (t == TvTab.Home) _ = page.Navigation.PopToRootAsync(false);
            else _ = Go(page, t == TvTab.Library ? new TvLibraryPage() : new TvSearchPage());
        }
        var home = Tab(ctx, TvL.T("nav.home", "Главная", "Home"), active == TvTab.Home, () => Open(TvTab.Home), onFocus: () => Open(TvTab.Home));
        var lib = Tab(ctx, TvL.T("home.library_title", "Библиотека", "Library"), active == TvTab.Library, () => Open(TvTab.Library), onFocus: () => Open(TvTab.Library));
        var search = Tab(ctx, TvL.T("search.title", "Поиск", "Search"), active == TvTab.Search, () => Open(TvTab.Search), onFocus: () => Open(TvTab.Search));
        View.AddView(home, TabLp(ctx, 0));
        View.AddView(lib, TabLp(ctx, 8));
        View.AddView(search, TabLp(ctx, 8));
        View.AddView(new AView(ctx), new LinearLayout.LayoutParams(0, 1, 1f));
        View.AddView(Tab(ctx, TvL.T("profile.title", "Профиль", "Profile"), false,
            () => _ = page.Navigation.PushAsync(new ProfilePanelPage()), solid: true), TabLp(ctx, 0));

        Active = active switch { TvTab.Library => lib, TvTab.Search => search, _ => home };
        ((BarLayout)View).Preferred = Active;
    }

    public LinearLayout.LayoutParams Lp(Context ctx) => new(-1, TvUi.Dp(ctx, 40));

    /// <summary>Open a tab page and keep the stack at [Home, tab].</summary>
    private static async Task Go(ContentPage from, Page target)
    {
        var nav = from.Navigation;
        await nav.PushAsync(target, false);
        foreach (var p in nav.NavigationStack.Skip(1).Where(p => p != target).ToList())
            nav.RemovePage(p);
    }

    // full bar height for every tab, so the focus pill is the same size everywhere
    private static LinearLayout.LayoutParams TabLp(Context ctx, int leftDp)
        => new(-2, -1) { LeftMargin = TvUi.Dp(ctx, leftDp) };

    private static TextView Tab(Context ctx, string text, bool selected, Action click, bool solid = false, Action? onFocus = null)
    {
        int focusSeq = 0;
        var t = TvUi.Text_(ctx, 15, selected ? TvUi.Text : TvUi.TextDim, TvUi.Medium, 1, text);
        t.Gravity = GravityFlags.Center;
        int hp = TvUi.Dp(ctx, 18);
        t.SetPadding(hp, 0, hp, 0);
        t.Focusable = true; t.Clickable = true; t.FocusableInTouchMode = false;
        var normal = selected || solid ? TvUi.Round(ctx, TvUi.Surface2, 20) : null;
        var focus = TvUi.Round(ctx, AColor.White, 20);
        t.Background = normal;
        t.FocusChange += (_, e) =>
        {
            t.Background = e.HasFocus ? focus : normal;
            t.SetTextColor(e.HasFocus ? AColor.ParseColor("#1a1a1a") : (selected || solid ? TvUi.Text : TvUi.TextDim));
            // Google-TV style: resting on a tab opens it (after a short pause, so
            // walking across the menu doesn't open every page on the way).
            var seq = ++focusSeq;
            if (e.HasFocus && !selected && onFocus is not null)
                t.PostDelayed(() => { if (seq == focusSeq && t.HasFocus) onFocus(); }, 350);
        };
        t.Click += (_, _) => click();
        return t;
    }
}
#endif
