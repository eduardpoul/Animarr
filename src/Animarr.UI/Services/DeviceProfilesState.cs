using Animarr.Shared;
using Animarr.Shared.Models;
using Microsoft.JSInterop;

namespace Animarr.UI.Services;

/// <summary>
/// Client view of THIS device's profiles — a cache of the SERVER's
/// <c>GET /api/auth/device</c> (profiles that signed in here, which of them
/// have a PIN on this device, the "Sign in by default" profile) plus the
/// per-session latch of the "Who's watching" startup picker.
///
/// PINs are no longer stored or checked here: they live on the server per
/// user per device, and every switch is verified there. The old device-local
/// registry (localStorage <c>animarr_device_profiles</c>, SHA-256 PINs) is
/// purged on first load — those PINs can't be trusted server-side, so their
/// users get the mandatory "Set a PIN" step on next use instead.
///
/// Session latch: the picker runs once per browser session / app launch. It
/// is kept in sessionStorage (survives a reload in the same tab, not a new
/// tab or a browser restart) and mirrored in memory (the MAUI WebView lives
/// exactly as long as the app process).
/// </summary>
public sealed class DeviceProfilesState
{
    private const string GateKey           = "animarr_profile_gate";
    private const string PendingDefaultKey = "animarr_pending_default";
    private const string LegacyKey         = "animarr_device_profiles";

    private readonly IAnimarrApiClient _api;
    private readonly IJSRuntime _js;
    private bool _gatePassed;
    private bool _legacyPurged;

    public DeviceProfilesState(IAnimarrApiClient api, IJSRuntime js)
    {
        _api = api;
        _js  = js;
    }

    /// <summary>Last state read from the server; null before the first read or
    /// when the server is older than per-device PINs (no /api/auth/device) —
    /// callers then skip the device gates entirely.</summary>
    public DeviceStateDto? State { get; private set; }

    public event Action? OnChange;

    /// <summary>Re-read the device state from the server. Returns null (and
    /// clears <see cref="State"/>) when the endpoint is unavailable.</summary>
    public async Task<DeviceStateDto?> RefreshAsync(CancellationToken ct = default)
    {
        await PurgeLegacyAsync();
        try { State = await _api.GetDeviceStateAsync(ct); }
        catch { State = null; }
        OnChange?.Invoke();
        return State;
    }

    /// <summary>Adopt a state returned by a mutation (e.g. the default toggle).</summary>
    public void Apply(DeviceStateDto state)
    {
        State = state;
        OnChange?.Invoke();
    }

    /// <summary>True when the signed-in user still has to set their PIN for
    /// this device — the blocking "Set a PIN" step. False when unknown.</summary>
    public bool CurrentUserNeedsPin =>
        State is { CurrentUserId: not null, CurrentUserHasPin: false };

    /// <summary>Profiles that can be entered by PIN on this device.</summary>
    public int PinnedProfileCount => State?.Profiles.Count(p => p.HasPin) ?? 0;

    public DeviceProfileDto? DefaultProfile =>
        State?.DefaultUserId is Guid id ? State.Profiles.FirstOrDefault(p => p.Id == id) : null;

    // ─── Startup-picker session latch ─────────────────────────────────────

    public async Task<bool> IsGatePassedAsync()
    {
        if (_gatePassed) return true;
        try { _gatePassed = await _js.InvokeAsync<string?>("sessionStorage.getItem", GateKey) == "1"; }
        catch { }
        return _gatePassed;
    }

    /// <summary>The user identified themselves this session (picked a profile,
    /// signed in, set a PIN, or the device default kicked in).</summary>
    public async Task MarkGatePassedAsync()
    {
        _gatePassed = true;
        try { await _js.InvokeVoidAsync("sessionStorage.setItem", GateKey, "1"); } catch { }
    }

    public async Task ResetGateAsync()
    {
        _gatePassed = false;
        try { await _js.InvokeVoidAsync("sessionStorage.removeItem", GateKey); } catch { }
    }

    // ─── Pending default ──────────────────────────────────────────────────
    // The picker's "Sign in by default" toggle applies to the profile picked.
    // When that profile can't finish in the picker (no PIN here yet → password
    // sign-in → Set-a-PIN), the choice is parked here and applied once the PIN
    // exists.

    public async Task SetPendingDefaultAsync(Guid? userId)
    {
        try
        {
            if (userId is Guid id) await _js.InvokeVoidAsync("sessionStorage.setItem", PendingDefaultKey, id.ToString());
            else await _js.InvokeVoidAsync("sessionStorage.removeItem", PendingDefaultKey);
        }
        catch { }
    }

    /// <summary>Apply (and clear) a parked default if it is for
    /// <paramref name="userId"/>, who must have a PIN by now.</summary>
    public async Task ApplyPendingDefaultAsync(Guid userId)
    {
        string? raw = null;
        try { raw = await _js.InvokeAsync<string?>("sessionStorage.getItem", PendingDefaultKey); } catch { }
        if (raw is null) return;
        await SetPendingDefaultAsync(null);
        if (!Guid.TryParse(raw, out var id) || id != userId) return;
        try
        {
            var state = await _api.SetDeviceDefaultAsync(new Shared.Requests.SetDeviceDefaultRequest(true));
            if (state is not null) Apply(state);
        }
        catch { }
    }

    /// <summary>Where to land after a sign-out: the picker when other
    /// PIN-protected profiles remain on this device, else the welcome page.</summary>
    public async Task<string> RouteAfterSignOutAsync()
    {
        await ResetGateAsync();
        var state = await RefreshAsync();
        return state?.Profiles.Any(p => p.HasPin) == true ? "/switch" : "/welcome";
    }

    private async Task PurgeLegacyAsync()
    {
        if (_legacyPurged) return;
        _legacyPurged = true;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", LegacyKey); } catch { }
    }
}
