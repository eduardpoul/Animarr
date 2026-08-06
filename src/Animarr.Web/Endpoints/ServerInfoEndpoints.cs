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

        // GET /api/server/dashboard — anonymous aggregates for external
        // dashboards (Homepage's customapi widget, polled every minute).
        // Same reasoning as /api/server/info above: no titles, no users, no
        // paths — just COUNT/SUM over indexed columns, cheap enough for a
        // SQLite database to answer once a minute forever. Deliberately
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
        }).AllowAnonymous();

        return app;
    }
}
