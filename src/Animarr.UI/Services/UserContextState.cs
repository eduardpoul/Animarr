using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;

namespace Animarr.UI.Services;

/// <summary>
/// Per-circuit (Server) / per-process (WASM) cache of the current user identity
/// + preferences. MainLayout calls <see cref="LoadAsync"/> on boot; Login /
/// Setup screens call it again after a successful POST so the topbar/sidebar
/// re-render with the new identity. Components subscribe to <see cref="OnChange"/>
/// to react to login/logout/role changes.
///
/// Holds:
///   • <see cref="AuthStatus"/> — anonymous + setup probe
///   • <see cref="Me"/> — the MeDto (null while anonymous)
///   • <see cref="Preferences"/> — UserPreferencesDto (null while anonymous)
///
/// Concurrency: not thread-safe — Blazor circuits are single-threaded and the
/// WASM client runs on a single .NET thread, so cross-thread access doesn't
/// happen in normal flow.
/// </summary>
public sealed class UserContextState
{
    private readonly IAnimarrApiClient _api;

    public UserContextState(IAnimarrApiClient api) { _api = api; }

    public AuthStatusDto?       AuthStatus  { get; private set; }
    public MeDto?               Me          { get; private set; }
    public UserPreferencesDto?  Preferences { get; private set; }

    public event Action? OnChange;

    /// <summary>True once <see cref="LoadAsync"/> has finished (success or 401).
    /// Components gate their first paint on this so they don't flash unauthenticated
    /// chrome before the cookie probe completes.</summary>
    public bool Loaded { get; private set; }

    public bool IsAuthenticated => Me is not null;
    public bool Can(Func<PermissionsDto, bool> selector) =>
        Me is not null && selector(Me.Permissions);

    /// <summary>Boot probe — calls <c>/api/auth/status</c> first (anonymous-safe),
    /// then if authenticated also fetches <c>/api/me</c> + <c>/api/me/preferences</c>.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            AuthStatus = await _api.GetAuthStatusAsync(ct);
            if (AuthStatus.Authenticated)
            {
                Me = await _api.GetMeAsync(ct);
                if (Me is not null)
                {
                    try { Preferences = await _api.GetMyPreferencesAsync(ct); }
                    catch { Preferences = null; }
                }
            }
            else
            {
                Me = null;
                Preferences = null;
            }
        }
        catch
        {
            // Network error / server down — leave state untouched but mark loaded
            // so the UI shows a fallback rather than hanging on a spinner forever.
        }
        Loaded = true;
        OnChange?.Invoke();
    }

    /// <summary>Apply a freshly-returned User from /auth/login or /auth/setup,
    /// then re-fetch the full /me payload to pick up permissions + folder scope.</summary>
    public async Task RefreshMeAsync(CancellationToken ct = default)
    {
        try
        {
            Me = await _api.GetMeAsync(ct);
            if (Me is not null)
            {
                try { Preferences = await _api.GetMyPreferencesAsync(ct); }
                catch { Preferences = null; }
                AuthStatus = AuthStatus is null
                    ? new AuthStatusDto(false, true)
                    : new AuthStatusDto(AuthStatus.SetupRequired, true);
            }
        }
        catch { }
        OnChange?.Invoke();
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { await _api.LogoutAsync(ct); } catch { }
        Me = null;
        Preferences = null;
        if (AuthStatus is not null)
            AuthStatus = new AuthStatusDto(AuthStatus.SetupRequired, false);
        OnChange?.Invoke();
    }

    /// <summary>Replace Preferences with the result of a PATCH — bubbles to all
    /// subscribers so accent/backdrop/audio updates propagate without an
    /// extra GET round-trip.</summary>
    public void SetPreferences(UserPreferencesDto prefs)
    {
        Preferences = prefs;
        OnChange?.Invoke();
    }
}
