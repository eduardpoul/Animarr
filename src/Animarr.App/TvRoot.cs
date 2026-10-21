using System.Linq;
using Microsoft.Maui.Storage;

namespace Animarr.App;

/// <summary>
/// Root-swap + persisted-server helpers for the native TV shell. The onboarding
/// steps (bootstrap → server pick → pairing → catalog) are full-screen stages,
/// not a back-stack, so each transition replaces the window's root page rather
/// than pushing onto a NavigationPage. Centralising it here keeps every screen
/// from re-deriving "which window am I in".
/// </summary>
public static class TvRoot
{
    /// <summary>Preferences key holding the chosen server's base URL. The native
    /// TV shell can't share the Blazor ServerRegistryState (that lives in the
    /// WebView's localStorage), so it persists its own pick here.</summary>
    public const string ServerKey = "animarr_tv_server_url";

    /// <summary>Replace the current window's root with <paramref name="page"/>
    /// wrapped in a fresh, chrome-less NavigationPage. Safe from any screen's
    /// code-behind.</summary>
    /// <summary>The TV home: the new native (Fluent, virtualised) screen on
    /// Android, the old XAML catalog elsewhere.</summary>
    public static Page Home()
#if ANDROID
        => new Tv.TvHomePage();
#else
        => new CatalogNativePage();
#endif

    /// <summary>The TV start screen (server discovery + pick).</summary>
    public static Page ServerPicker()
#if ANDROID
        => new Tv.TvServerPickerPage();
#else
        => new ServerPickerPage();
#endif

    public static void Go(Page page)
    {
        var win = Application.Current?.Windows.FirstOrDefault();
        if (win is not null)
            win.Page = new NavigationPage(page);
    }
}
