using System.Reflection;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// <c>GET /api/server/info</c> — anonymous server-identity probe used by the
/// v5 multi-server flow. The client picks one of N known servers (registry
/// in localStorage) by hitting this endpoint on each candidate URL and
/// reading the returned <see cref="ServerInfoDto"/> to render the picker.
///
/// The endpoint is intentionally NOT [Authorize] — clients need a way to
/// identify a server before they have a cookie for it. The payload is
/// lightweight enough (no user list, no media, no secrets) that exposing it
/// to the LAN is acceptable; the Discovery flow assumes the client is on
/// the same network as the server.
/// </summary>
internal static class ServerInfoEndpoints
{
    /// <summary>AppConfig key for the persistent install GUID. Created once
    /// on first probe and never rotated.</summary>
    private const string ServerIdKey   = "server.id";
    /// <summary>AppConfig key for the operator-configurable display name.
    /// Defaults to "Animarr" when unset.</summary>
    private const string ServerNameKey = "server.name";

    public static IEndpointRouteBuilder MapServerInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.ServerInfo, async (
            IAppConfigService config,
            IDbContextFactory<AppDbContext> dbFactory,
            AuthService auth,
            CancellationToken ct) =>
        {
            // Lazy-create the server identity GUID. Once written, it survives
            // container restarts (AppConfig lives in the same SQLite file as
            // the rest of the catalog). Two parallel first-boot probes can
            // race here — both write the same GUID column; the second SetAsync
            // simply overwrites with an identical value because AppConfigService
            // upserts. So we read AGAIN after the write, in case another
            // concurrent caller wrote first and we'd otherwise return a GUID
            // that didn't make it to disk.
            var serverId = await config.GetAsync(ServerIdKey, ct);
            if (string.IsNullOrWhiteSpace(serverId))
            {
                var fresh = Guid.NewGuid().ToString();
                await config.SetAsync(ServerIdKey, fresh, ct);
                serverId = await config.GetAsync(ServerIdKey, ct) ?? fresh;
            }

            var name = await config.GetAsync(ServerNameKey, ct);
            if (string.IsNullOrWhiteSpace(name))
                name = "Animarr";

            // Lightweight title count — COUNT(*) on the indexed primary table.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var titleCount = await db.MediaItems.CountAsync(ct);

            // Setup required iff the Users table is empty. Routes the client
            // to /setup instead of /login after picking this server.
            var setupRequired = await auth.IsSetupRequiredAsync(ct);

            var version = typeof(ServerInfoEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(ServerInfoEndpoints).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            return Results.Ok(new ServerInfoDto(
                Name:          name!,
                Version:       version,
                Mode:          "multi-user",
                TitleCount:    titleCount,
                SetupRequired: setupRequired,
                ServerId:      serverId!));
        }).AllowAnonymous();

        // GET /api/server/dashboard — aggregates for external dashboards
        // (Homepage's customapi widget, polled every minute). No cookie — the
        // dashboard authenticates with the admin-generated API key, sent as
        // the X-Api-Key header or ?key= (see DashboardKeyFilter). No key set
        // → 401 for everyone. No titles, no users, no paths — just COUNT/SUM
        // over indexed columns, cheap enough for a SQLite database to answer
        // once a minute forever. Deliberately
        // excludes on-disk library size (would mean walking the filesystem)
        // and download SPEED (TorrentRecord only has cumulative Downloaded/
        // Uploaded, not bytes/sec — that lives in the live torrent engine,
        // not the DB, and would need a different source if ever added).
        //
        // EpisodesTotal is EpisodeFileMappings' near-namesake NOT what it
        // sounds like: that table only holds manual/LLM per-file (season,
        // episode) OVERRIDES (see EpisodeFileMapping's own doc comment) — the
        // deterministic majority case is resolved on the fly by
        // MediaFileResolver and never persisted, so there is no cheap DB-only
        // "episodes on disk" count. Sum(MediaItem.EpisodeCount) — a
        // denormalised, metadata-sourced total already cached for the catalog
        // grid — is the closest cheap proxy for library size instead.
        app.MapGet(ApiRoutes.ServerDashboard, async (
            IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var weekAgo = DateTime.UtcNow.AddDays(-7);

            var downloading = db.TorrentRecords
                .Where(t => t.State == Animarr.Web.Data.Models.TorrentRecordState.Downloading);

            return Results.Ok(new ServerDashboardDto(
                Titles:                await db.MediaItems.CountAsync(ct),
                TitlesAddedWeek:       await db.MediaItems.CountAsync(m => m.CreatedAt >= weekAgo, ct),
                EpisodesTotal:         await db.MediaItems.SumAsync(m => m.EpisodeCount ?? 0, ct),
                TorrentsDownloading:   await downloading.CountAsync(ct),
                TorrentsSeeding:       await db.TorrentRecords
                                           .CountAsync(t => t.State == Animarr.Web.Data.Models.TorrentRecordState.Seeding, ct),
                DownloadingBytesTotal: await downloading.SumAsync(t => t.TotalSize, ct),
                DownloadingBytesDone:  await downloading.SumAsync(t => t.Downloaded, ct)));
        })
        // AllowAnonymous only at the cookie layer: the API-key filter is the gate.
        .AllowAnonymous()
        .AddEndpointFilter(DashboardKeyFilter);

        // ─── Dashboard API key management (admin) ──────────────────────
        // GET shows the current key, POST generates a fresh one (the old key
        // stops working at once), DELETE revokes it (dashboard → 401). Stored
        // encrypted in AppConfig (SecretProtector) — no extra table.
        var keyAdmin = app.MapGroup(ApiRoutes.ServerDashboardKey)
            .RequireAuthorization(AuthConstants.Policies.SystemSettings);

        keyAdmin.MapGet(string.Empty, async (IAppConfigService config, CancellationToken ct) =>
            Results.Ok(new DashboardKeyDto(NullIfEmpty(await config.GetAsync(Animarr.Web.Data.Models.AppConfigKeys.DashboardApiKey, ct)))));

        keyAdmin.MapPost(string.Empty, async (IAppConfigService config, CancellationToken ct) =>
        {
            // 32 random bytes → 43-char URL-safe string (fits a query param as-is).
            var key = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await config.SetAsync(Animarr.Web.Data.Models.AppConfigKeys.DashboardApiKey, key, ct);
            return Results.Ok(new DashboardKeyDto(key));
        });

        keyAdmin.MapDelete(string.Empty, async (IAppConfigService config, CancellationToken ct) =>
        {
            await config.SetAsync(Animarr.Web.Data.Models.AppConfigKeys.DashboardApiKey, null, ct);
            return Results.Ok(new DashboardKeyDto(null));
        });

        return app;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Lets the request through only with the configured dashboard
    /// API key in <c>X-Api-Key</c> or <c>?key=</c>. Constant-time compare.</summary>
    private static async ValueTask<object?> DashboardKeyFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http     = ctx.HttpContext;
        var config   = http.RequestServices.GetRequiredService<IAppConfigService>();
        var expected = NullIfEmpty(await config.GetAsync(Animarr.Web.Data.Models.AppConfigKeys.DashboardApiKey, http.RequestAborted));

        var supplied = http.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(supplied)) supplied = http.Request.Query["key"].ToString();

        if (expected is null || string.IsNullOrEmpty(supplied)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(supplied),
                   System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Unauthorized();
        }
        http.Response.Headers.CacheControl = "no-store";
        return await next(ctx);
    }
}
