namespace Animarr.Shared.Models;

/// <summary>
/// Identifies a single Animarr server install. Used by the v5 multi-server
/// discovery flow — the client probes <c>GET /api/server/info</c> on each
/// candidate URL to populate the server picker (Discovery page) and to
/// hydrate registered-server cards (TopBar pill, ProfilePanel server tab).
///
/// Anonymous-accessible — clients need to identify a server BEFORE they have
/// a valid auth cookie for it. Carries only identification + lightweight
/// stats (title count, version, mode) — no per-user or media data.
/// </summary>
public sealed record ServerInfoDto(
    /// <summary>Display name (configurable via AppConfig key "server.name",
    /// default "Animarr"). Appears in the TopBar pill and Discovery cards.</summary>
    string Name,

    /// <summary>Assembly informational version of the running Animarr.Web
    /// process, e.g. "5.0.0+abc123".</summary>
    string Version,

    /// <summary>"single-user" or "multi-user". v5 servers always report
    /// "multi-user" but the field is reserved so older clients reading newer
    /// servers (and vice versa) can branch on capability.</summary>
    string Mode,

    /// <summary>Total MediaItems in the library. Cheap COUNT(*) query.</summary>
    int TitleCount,

    /// <summary>True iff the Users table is empty. Tells the client to route
    /// to /setup (not /login) after picking this server in Discovery.</summary>
    bool SetupRequired,

    /// <summary>Stable GUID per install — survives container restarts and
    /// volume migrations because it's persisted in AppConfig under
    /// <c>"server.id"</c>. The server registry keys entries on this so a
    /// server moving from 192.168.1.50 to a Tailscale hostname doesn't
    /// duplicate.</summary>
    string ServerId);

/// <summary>
/// One entry in the client-side server registry. Persisted to localStorage
/// under key <c>animarr.servers.v1</c> (see <c>ServerRegistryState</c>).
/// Captures everything the picker UI needs to render a card without re-probing
/// the server on every render.
/// </summary>
public sealed record RegisteredServerDto(
    string ServerId,
    string Name,
    string BaseUrl,
    DateTime LastSeenUtc,
    string? Version,
    int? TitleCount);
