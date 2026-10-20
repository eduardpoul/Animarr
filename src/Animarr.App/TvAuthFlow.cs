using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Native TV counterpart of the Blazor device gates (MainLayout's
/// RunDeviceGatesAsync), on the same server API:
///
///   • app launch (<see cref="RouteOnOpenAsync"/>) — the device's "Sign in by
///     default" profile → straight in (no picker, no PIN); else ≥2 profiles on
///     this TV → the "Who's watching" picker; anonymous with PIN profiles left
///     → the picker; else pairing.
///   • after any sign-in (<see cref="AfterSignInAsync"/>) — a profile without
///     a PIN on this TV gets the blocking "Set a PIN" page first; else catalog.
///
/// The TV process lives exactly one app session, so "once per launch" needs no
/// latch: <see cref="RouteOnOpenAsync"/> runs from the bootstrap page only.
/// A server without /api/auth/device (older build) keeps the plain
/// signed-in → catalog / anonymous → pairing routing.
/// </summary>
internal static class TvAuthFlow
{
    private static IServiceProvider Services => IPlatformApplication.Current?.Services
        ?? throw new InvalidOperationException("MAUI DI container not ready.");

    public static IAnimarrApiClient Api => Services.GetRequiredService<IAnimarrApiClient>();
    public static HttpClient Http => Services.GetRequiredService<HttpClient>();

    /// <summary>The TV's device state, or null when the server predates
    /// per-device profiles (or is unreachable).</summary>
    public static async Task<DeviceStateDto?> TryGetDeviceAsync()
    {
        try { return await Api.GetDeviceStateAsync(); }
        catch { return null; }
    }

    /// <summary>App-launch routing (server already chosen).</summary>
    public static async Task RouteOnOpenAsync()
    {
        var api = Api;
        await TvL.LoadLastAsync(Http);
        var status = await api.GetAuthStatusAsync();
        var dev = await TryGetDeviceAsync();
        if (dev is null)
        {
            TvRoot.Go(status.Authenticated ? new CatalogNativePage() : new PairingPage());
            return;
        }

        if (!status.Authenticated)
        {
            if (dev.DefaultUserId is not null && await TryEnterDefaultAsync())
            {
                await AfterSignInAsync();
                return;
            }
            TvRoot.Go(dev.Profiles.Any(p => p.HasPin)
                ? new WhosWatchingPage(WhosWatchingPage.PickerMode.Startup)
                : new PairingPage());
            return;
        }

        if (dev.DefaultUserId is Guid defaultId)
        {
            // "Always go straight in as that user" — even when someone else's
            // session was left behind on the TV.
            if (defaultId != dev.CurrentUserId) await TryEnterDefaultAsync();
            await AfterSignInAsync();
            return;
        }
        if (dev.Profiles.Length >= 2)
        {
            TvRoot.Go(new WhosWatchingPage(WhosWatchingPage.PickerMode.Startup));
            return;
        }
        await AfterSignInAsync();
    }

    /// <summary>After a sign-in / switch: load the profile's language, then the
    /// mandatory "Set a PIN" step when it has none on this TV, else the catalog.</summary>
    public static async Task AfterSignInAsync(bool makeDefaultAfterPin = false)
    {
        await LoadUserLanguageAsync();
        var dev = await TryGetDeviceAsync();
        if (dev is { CurrentUserId: not null, CurrentUserHasPin: false })
        {
            TvRoot.Go(TvPinPage.ForSetup(makeDefaultAfterPin));
            return;
        }
        TvRoot.Go(new CatalogNativePage());
    }

    /// <summary>Sign out of this TV: the server also removes the profile (and
    /// its PIN, and the default if it was this profile) from the device. Then
    /// the launch routing decides between the picker and pairing.</summary>
    public static async Task SignOutAsync()
    {
        try { await Api.LogoutAsync(); } catch { }
        try { await RouteOnOpenAsync(); }
        catch { TvRoot.Go(new PairingPage()); }
    }

    /// <summary>Apply the picker's "Sign in by default" toggle to the profile
    /// that is now signed in: true → it becomes the default, false → clear any
    /// default, null → leave as is.</summary>
    public static async Task ApplyDefaultAsync(bool? makeDefault)
    {
        if (makeDefault is not bool on) return;
        try { await Api.SetDeviceDefaultAsync(new SetDeviceDefaultRequest(on)); }
        catch { /* the sign-in itself worked; the default can be fixed in the profile menu */ }
    }

    private static async Task<bool> TryEnterDefaultAsync()
    {
        try { return await Api.EnterDeviceDefaultAsync() is not null; }
        catch { return false; }
    }

    private static async Task LoadUserLanguageAsync()
    {
        try
        {
            var prefs = await Api.GetMyPreferencesAsync();
            await TvL.LoadAsync(Http, prefs.Language);
        }
        catch { /* keep the current pack */ }
    }

    /// <summary>Friendly text for a failed PIN check.</summary>
    public static string PinError(PinAuthResult r, int min, int max) => r.Status switch
    {
        PinAuthStatus.WrongPin when r.AttemptsLeft is int left =>
            TvL.F("pin.wrong_attempts_fmt", "Неверный PIN — осталось попыток: {0}", "Wrong PIN — {0} tries left", left),
        PinAuthStatus.WrongPin =>
            TvL.T("pin.invalid", "Неверный PIN — попробуйте ещё раз", "Wrong PIN — try again"),
        PinAuthStatus.Locked =>
            TvL.F("pin.locked_fmt", "Слишком много неверных PIN. Повторите через {0} мин.",
                "Too many wrong PINs. Try again in {0} min.",
                Math.Max(1, (int)Math.Ceiling((r.RetryAfterSeconds ?? 300) / 60.0))),
        PinAuthStatus.Invalid =>
            TvL.F("setpin.err_length_fmt", "PIN должен состоять из {0}–{1} цифр.", "The PIN must be {0}–{1} digits.", min, max),
        PinAuthStatus.PinNotSet or PinAuthStatus.NotFound =>
            TvL.T("switch.profile_gone",
                "Этот профиль больше нельзя открыть по PIN на этом устройстве. Войдите с его паролем.",
                "That profile can't be opened with a PIN here any more. Sign in with its password."),
        _ => TvL.T("profile.err_switch_failed", "Не удалось переключиться — попробуйте снова.", "Couldn't switch — try again."),
    };
}
