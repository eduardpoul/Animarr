using System.Text.Json;
using Animarr.Shared;
using Animarr.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Animarr.UI.Services;

/// <summary>
/// Per-circuit / per-app cache of the client's known Animarr servers.
///
/// v5 introduces the multi-server flow: a single Animarr client (WASM in a
/// browser, or MAUI Hybrid on phone/desktop) can talk to N servers and switch
/// between them without re-installing. This service holds the registry and
/// surfaces the "currently active" server to the rest of the UI.
///
/// Persistence:
///   • Browser/WASM — serialised to <c>localStorage</c> under key
///     <c>animarr.servers.v1</c>. Survives tab reloads and page navigations.
///   • MAUI — same localStorage key via the embedded WebView's storage; the
///     OS provides per-app sandboxing so the registry is private to Animarr.
///
/// When the active server changes (LoadAsync hydrates Current /
/// AddServerAsync registers the first server / SetCurrentAsync switches), the
/// shared <see cref="ServerAddressProvider"/> is updated so subsequent API
/// calls land on the chosen origin. We deliberately route through the
/// provider instead of mutating <see cref="HttpClient.BaseAddress"/> directly
/// — once any request has been sent, HttpClient locks the setter and throws
/// <see cref="InvalidOperationException"/>. The provider + DelegatingHandler
/// pair sidesteps that completely.
///
/// Cookies stay in the shared <see cref="System.Net.CookieContainer"/>; the
/// container filters them per origin so an old tower.one cookie is harmless
/// against 192.168.x.
///
/// Concurrency: not thread-safe — Blazor circuits and WASM are single-threaded.
/// </summary>
public sealed class ServerRegistryState
{
    /// <summary>localStorage key. Bumped to v2/v3 if the entry shape changes
    /// so old clients don't crash on a deserialise mismatch.</summary>
    private const string StorageKey = "animarr.servers.v1";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime             _js;
    private readonly IAnimarrApiClient      _api;
    private readonly ServerAddressProvider  _addr;
    private readonly NavigationManager      _nav;

    private readonly List<RegisteredServerDto> _servers = new();
    private string? _currentServerId;
    private bool _loaded;

    public ServerRegistryState(IJSRuntime js, IAnimarrApiClient api,
        ServerAddressProvider addr, NavigationManager nav)
    {
        _js   = js;
        _api  = api;
        _addr = addr;
        _nav  = nav;
    }

    /// <summary>All known servers, ordered by most-recently-seen first.
    /// Components should subscribe to <see cref="OnChange"/> instead of caching
    /// snapshots — the list mutates in place.</summary>
    public IReadOnlyList<RegisteredServerDto> Servers => _servers;

    /// <summary>The server we're currently authenticated against (or trying to
    /// log into). Null while the registry is empty / before first probe.</summary>
    public RegisteredServerDto? Current =>
        _currentServerId is null
            ? null
            : _servers.FirstOrDefault(s => s.ServerId == _currentServerId);

    public int Count => _servers.Count;

    /// <summary>True once <see cref="LoadAsync"/> has run — components gate
    /// their first paint on this so the empty state doesn't flash on boot.</summary>
    public bool Loaded => _loaded;

    public event Action? OnChange;

    /// <summary>Hydrate from localStorage. Safe to call multiple times — only
    /// the first call hits storage; subsequent calls no-op.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", ct, StorageKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var blob = JsonSerializer.Deserialize<RegistryBlob>(raw!, JsonOpts);
                if (blob is not null)
                {
                    _servers.Clear();
                    if (blob.Servers is not null) _servers.AddRange(blob.Servers);
                    _currentServerId = blob.CurrentServerId;
                }
            }
        }
        catch
        {
            // Storage unavailable (e.g. server-prerender pass, locked-down
            // browser policy) — start empty. The user can add servers manually.
        }
        _loaded = true;
        // Reapply Current to the shared HttpClient so cold-start API calls hit
        // the user's chosen server, not whatever compile-time DefaultServerUrl
        // MauiProgram pinned into HttpClient at boot.
        ApplyCurrentToHttp();

        // If the registry is empty but we were SERVED FROM a real Animarr origin
        // — i.e. the WASM web client running in a browser, as opposed to the
        // MAUI app whose page lives at the 0.0.0.x virtual host — adopt that
        // origin automatically. A browser hitting the server directly is, by
        // definition, already talking to a server; making it sit on a "No
        // servers found" discovery screen (which can't even mDNS-probe from a
        // browser) is wrong. This also self-heals after the user clears site
        // data, which wipes this localStorage registry.
        if (_servers.Count == 0)
            await TryAdoptServingOriginAsync(ct);

        OnChange?.Invoke();
    }

    /// <summary>
    /// Probe + register the origin this client was served from, when the
    /// registry is otherwise empty. No-op on the MAUI host (its page origin is
    /// the <c>https://0.0.0.x/</c> virtual host, not a reachable server — MAUI
    /// uses mDNS / subnet discovery + an explicit server pick instead).
    /// </summary>
    private async Task TryAdoptServingOriginAsync(CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(_nav.BaseUri, UriKind.Absolute, out var baseUri)) return;
            if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) return;
            // MAUI BlazorWebView virtual host — not a real server.
            if (baseUri.Host.StartsWith("0.0.0.", StringComparison.Ordinal)) return;

            var origin = baseUri.GetLeftPart(UriPartial.Authority);  // scheme://host[:port]
            // AddServerAsync probes /api/server/info; on success it registers
            // the entry AND makes it Current (registry was empty).
            await AddServerAsync(origin, ct);
        }
        catch
        {
            // Probe failed (server briefly unreachable, etc.) — leave the
            // registry empty so the discovery / manual-URL screen still shows.
        }
    }

    /// <summary>Probe <paramref name="baseUrl"/> with /api/server/info and add
    /// the result to the registry (or refresh an existing entry by ServerId).
    /// Returns the registered entry on success, null if the probe failed.
    /// If the registry was empty, the new server is set as Current.</summary>
    public async Task<RegisteredServerDto?> AddServerAsync(string baseUrl, CancellationToken ct = default)
    {
        var info = await _api.GetServerInfoAsync(baseUrl, ct);
        if (info is null) return null;

        var normalised = NormaliseUrl(baseUrl);
        var entry = new RegisteredServerDto(
            ServerId:    info.ServerId,
            Name:        info.Name,
            BaseUrl:     normalised,
            LastSeenUtc: DateTime.UtcNow,
            Version:     info.Version,
            TitleCount:  info.TitleCount);

        await UpsertAsync(entry, ct);
        return entry;
    }

    /// <summary>Add or refresh a pre-built registry entry without re-probing
    /// /api/server/info. Used when an upstream layer already knows enough
    /// about the server (mDNS / NSD resolved the SRV+TXT records, subnet
    /// probe parsed the JSON itself) and forcing another HTTP round trip
    /// would lose the entry if Android's HttpClient happens to fail. Dedupes
    /// by ServerId. If the registry was empty, the new entry becomes Current
    /// so the user lands on the right login screen.</summary>
    public async Task UpsertAsync(RegisteredServerDto entry, CancellationToken ct = default)
    {
        var existingIdx = _servers.FindIndex(s => s.ServerId == entry.ServerId);
        if (existingIdx >= 0)
        {
            // Preserve the existing TitleCount if the new entry doesn't have
            // one — the scan-time path doesn't know it without an HTTP probe.
            var prev = _servers[existingIdx];
            var merged = entry with
            {
                TitleCount = entry.TitleCount ?? prev.TitleCount,
                Version    = entry.Version    ?? prev.Version,
            };
            _servers[existingIdx] = merged;
        }
        else
        {
            _servers.Add(entry);
        }

        var becameCurrent = _currentServerId is null;
        _currentServerId ??= entry.ServerId;

        await PersistAsync(ct);
        if (becameCurrent) ApplyCurrentToHttp();
        OnChange?.Invoke();
    }

    /// <summary>Drop a server from the registry. If it was the current one,
    /// the next-most-recent server (if any) takes its place.</summary>
    public async Task RemoveServerAsync(string serverId, CancellationToken ct = default)
    {
        var idx = _servers.FindIndex(s => s.ServerId == serverId);
        if (idx < 0) return;
        _servers.RemoveAt(idx);

        if (_currentServerId == serverId)
        {
            _currentServerId = _servers
                .OrderByDescending(s => s.LastSeenUtc)
                .Select(s => s.ServerId)
                .FirstOrDefault();
        }

        await PersistAsync(ct);
        OnChange?.Invoke();
    }

    /// <summary>Mark <paramref name="serverId"/> as the active server. Updates
    /// the shared HttpClient's BaseAddress so the very next API call lands on
    /// the chosen origin. In WASM the caller still wants a hard navigate to
    /// pick up cookies for the new domain; in MAUI the WebView never reloads,
    /// so a regular Nav.NavigateTo("/login") is enough once BaseAddress flipped.</summary>
    public async Task SetCurrentAsync(string serverId, CancellationToken ct = default)
    {
        if (_servers.All(s => s.ServerId != serverId)) return;
        if (_currentServerId == serverId) return;
        _currentServerId = serverId;
        await PersistAsync(ct);
        ApplyCurrentToHttp();
        OnChange?.Invoke();
    }

    /// <summary>Set a friendly client-side name for an existing server entry.
    /// Pure local rename — does NOT call the server, so two different clients
    /// can pick different names for the same install (the server's own name
    /// stays whatever AppConfig "server.name" holds and shows up only on a
    /// re-probe via <see cref="AddServerAsync"/>). Empty/whitespace falls back
    /// to the last probed name; we never persist an unnamed entry because the
    /// picker UI would render blank rows.</summary>
    public async Task RenameAsync(string serverId, string newName, CancellationToken ct = default)
    {
        var idx = _servers.FindIndex(s => s.ServerId == serverId);
        if (idx < 0) return;
        var trimmed = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        var existing = _servers[idx];
        if (string.Equals(existing.Name, trimmed, StringComparison.Ordinal)) return;
        _servers[idx] = existing with { Name = trimmed };
        await PersistAsync(ct);
        OnChange?.Invoke();
    }

    /// <summary>Clear the registry. Used by the "remove all servers" path —
    /// not exposed in the UI but useful for tests and debug.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        _servers.Clear();
        _currentServerId = null;
        await PersistAsync(ct);
        OnChange?.Invoke();
    }

    // ─── Internals ──────────────────────────────────────────────────────

    private async Task PersistAsync(CancellationToken ct)
    {
        try
        {
            var blob = new RegistryBlob(_servers.ToList(), _currentServerId);
            var json = JsonSerializer.Serialize(blob, JsonOpts);
            await _js.InvokeVoidAsync("localStorage.setItem", ct, StorageKey, json);
        }
        catch
        {
            // localStorage write can fail in prerender or Safari private mode.
            // The registry stays correct in-memory; next reload may rehydrate
            // from a stale blob, which is preferable to crashing the UI.
        }
    }

    /// <summary>Trim trailing slash, force scheme. Used so two adds for
    /// "http://x/" and "http://x" don't dedupe via string compare even though
    /// ServerId already protects against that.</summary>
    private static string NormaliseUrl(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Contains("://", StringComparison.Ordinal)) return trimmed;
        // Bare hostname/IP entered manually — default to http://.
        return "http://" + trimmed;
    }

    /// <summary>
    /// Point <see cref="ServerAddressProvider"/> at <see cref="Current"/>'s
    /// URL so subsequent API calls — composed against HttpClient's placeholder
    /// BaseAddress and then rewritten by <see cref="ServerAddressHandler"/>
    /// just before send — hit the chosen server. No-op when Current is null
    /// or when the provider is already pointed at the right authority.
    ///
    /// Trailing slash matters: when Uri composes against a base, the segment
    /// before the last `/` is treated as a directory and anything past it as
    /// the file. Without the slash, "/api/me" gets resolved as a sibling of
    /// the server URL's last path segment and the path drops on the floor.
    /// </summary>
    private void ApplyCurrentToHttp()
    {
        var cur = Current;
        if (cur is null) return;

        var target = new Uri(cur.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

        if (_addr.Current is not null &&
            string.Equals(_addr.Current.GetLeftPart(UriPartial.Authority),
                          target.GetLeftPart(UriPartial.Authority),
                          StringComparison.OrdinalIgnoreCase))
            return;

        _addr.Current = target;
    }

    /// <summary>Serialisable shape — owns both the list and the
    /// currently-selected ID so localStorage carries everything we need.</summary>
    private sealed record RegistryBlob(
        List<RegisteredServerDto>? Servers,
        string?                    CurrentServerId);
}
