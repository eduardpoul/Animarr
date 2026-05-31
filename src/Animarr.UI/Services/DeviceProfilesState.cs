using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Animarr.UI.Services;

/// <summary>
/// DEVICE-LOCAL profile registry (v5.1). The switch-user / "who's watching"
/// picker is per-DEVICE, not per-server: it lists only the accounts that have
/// actually signed in on THIS device, and each can carry a PIN that is stored
/// and verified LOCALLY (never sent to the server). This matches how profile
/// PINs work on a shared device (TV / console): the lock lives on the device,
/// not as a global account flag — so a PIN set in a browser does NOT surface
/// on a phone where you only signed into one account.
///
/// Storage: one localStorage key holding a JSON array of { Id, Pin } entries
/// (Pin = SHA-256 hex of the 4-digit code, or null). Profile name/avatar are
/// deliberately NOT cached here — they're re-read from the server roster and
/// intersected with these ids, so renames / avatar changes always show fresh.
///
/// Security: client-side PIN verification is intentionally light. The threat
/// model is "keep a kid from tapping into a parent's profile on the couch",
/// not a determined attacker (who could clear localStorage or hit the API
/// directly). The server no longer gates fast-switching by PIN — see
/// AuthEndpoints' switch-user.
/// </summary>
public sealed class DeviceProfilesState
{
    private readonly IJSRuntime _js;
    private const string Key = "animarr_device_profiles";

    public DeviceProfilesState(IJSRuntime js) { _js = js; }

    public sealed record Entry(string Id, string? Pin);

    public async Task<List<Entry>> GetAllAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", Key);
            if (string.IsNullOrWhiteSpace(json)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(json) ?? new();
        }
        catch { return new(); }
    }

    private async Task SaveAsync(List<Entry> list)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(list)); }
        catch { }
    }

    /// <summary>Register a profile on this device (idempotent). Called after a
    /// successful sign-in / setup / switch so the account becomes switchable.</summary>
    public async Task EnsureAsync(Guid userId)
    {
        var id = userId.ToString();
        var list = await GetAllAsync();
        if (list.Any(e => e.Id == id)) return;
        list.Add(new Entry(id, null));
        await SaveAsync(list);
    }

    /// <summary>Forget a profile on this device (e.g. "remove from this device").</summary>
    public async Task RemoveAsync(Guid userId)
    {
        var id = userId.ToString();
        var list = await GetAllAsync();
        if (list.RemoveAll(e => e.Id == id) > 0) await SaveAsync(list);
    }

    /// <summary>Ids of every profile registered on this device.</summary>
    public async Task<HashSet<Guid>> GetIdsAsync()
    {
        var set = new HashSet<Guid>();
        foreach (var e in await GetAllAsync())
            if (Guid.TryParse(e.Id, out var g)) set.Add(g);
        return set;
    }

    /// <summary>True iff this profile has a device-local PIN set on THIS device.</summary>
    public async Task<bool> HasPinAsync(Guid userId)
    {
        var id = userId.ToString();
        return (await GetAllAsync()).Any(e => e.Id == id && !string.IsNullOrEmpty(e.Pin));
    }

    /// <summary>Set (non-null raw code) or clear (null) the device-local PIN for
    /// a profile. The raw 4-digit code is hashed before storage.</summary>
    public async Task SetPinAsync(Guid userId, string? rawPin)
    {
        var id = userId.ToString();
        var hash = string.IsNullOrEmpty(rawPin) ? null : HashPin(rawPin);
        var list = await GetAllAsync();
        var idx = list.FindIndex(e => e.Id == id);
        if (idx < 0) list.Add(new Entry(id, hash));
        else list[idx] = list[idx] with { Pin = hash };
        await SaveAsync(list);
    }

    /// <summary>Verify a typed PIN against the device-local hash. Returns true
    /// when the profile has no PIN set (nothing to gate).</summary>
    public async Task<bool> VerifyPinAsync(Guid userId, string rawPin)
    {
        var id = userId.ToString();
        var entry = (await GetAllAsync()).FirstOrDefault(e => e.Id == id);
        if (entry is null || string.IsNullOrEmpty(entry.Pin)) return true;
        return HashPin(rawPin) == entry.Pin;
    }

    private static string HashPin(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("animarr-device-pin" + raw));
        return Convert.ToHexString(bytes);
    }
}
