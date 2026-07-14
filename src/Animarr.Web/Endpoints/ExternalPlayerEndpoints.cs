using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Endpoints for native external players (mpv + its animarr-tracker.lua):
/// a progress-ping sink that maps a disk path back to a MediaItem + (season,
/// episode) and writes watch state, plus the installable tracker script with a
/// baked-in server URL. AllowAnonymous — the mpv script has no auth cookie.
/// </summary>
internal static class ExternalPlayerEndpoints
{
    public static IEndpointRouteBuilder MapExternalPlayerEndpoints(this IEndpointRouteBuilder app)
    {
    // ─── /api/watch/external-progress — progress pings from external players ─
    // Used by mpv's animarr-tracker.lua script (installed once into mpv's
    // scripts/ dir). Resolves the file path back to a MediaItem + (season,
    // episode) and writes a WatchState row exactly as the in-browser player
    // would. Lets users open files in mpv for native HDR/Atmos/DV playback
    // without losing the Continue / % watched bookkeeping.
    app.MapPost("/api/watch/external-progress", async (
            ExternalProgressRequest body,
            MediaPathValidator pathValidator,
            IDbContextFactory<AppDbContext> dbFactory,
            IPatternMatchService patternMatch,
            IWatchStateService watchSvc,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
    {
        var logger = loggerFactory.CreateLogger("ExternalProgress");
        if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest("path required");

        var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(body.Path);
        if (!ok) return early!;

        // Resolve which MediaItem owns this path. Two cases:
        //   • Movie (SingleFilePath set): match path EXACTLY
        //   • Series / per-title folder: file lives under folder.Path
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.MediaItems.Include(m => m.Folder).ToListAsync(ct);

        MediaItem? owner = null;
        foreach (var m in items)
        {
            if (m.Folder is null) continue;
            if (!string.IsNullOrEmpty(m.Folder.SingleFilePath)
                && string.Equals(m.Folder.SingleFilePath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                owner = m; break;
            }
            if (!string.IsNullOrEmpty(m.Folder.Path) && !m.Folder.IsSection)
            {
                var folderRoot = m.Folder.Path
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath!.StartsWith(folderRoot, StringComparison.OrdinalIgnoreCase))
                {
                    owner = m; break;
                }
            }
        }
        if (owner is null)
        {
            logger.LogDebug("No MediaItem matches external play path {Path}", fullPath);
            return Results.NotFound("No catalog item owns this path");
        }

        int? season = null, episode = null;
        if (owner.MediaType != MediaItemType.Movie)
        {
            // Parse season/episode from filename using the same pattern engine
            // that drives rename suggestions — fileNameWithoutExt → (season, ep).
            var rules = await db.RenamePatterns
                .Where(p => p.Scope == PatternScope.Global)
                .ToListAsync(ct);
            var parsed = patternMatch.ParseFileName(Path.GetFileName(fullPath!), rules);
            if (parsed.IsMatched && parsed.Episode > 0)
            {
                // Season comes from filename or falls back to folder-path detection.
                season  = parsed.Season ?? patternMatch.DetectSeasonFromPath(
                    Path.GetDirectoryName(fullPath!) ?? "", owner.Folder?.Path) ?? 1;
                episode = parsed.Episode;
            }
        }

        var positionMs = (long)Math.Max(0, body.PositionSec * 1000);
        var runtimeMs  = body.DurationSec is > 0 ? (long?)(body.DurationSec * 1000) : null;
        // Delta tracking — clamp to reasonable per-tick increments so a stuck
        // mpv that keeps reporting position 0 doesn't inflate TotalWatchTimeSec.
        var delta = Math.Clamp(body.PlayedDeltaSec ?? 5, 0, 30);

        await watchSvc.RecordProgressAsync(owner.Id, season, episode, fullPath,
            positionMs, runtimeMs, delta, ct);

        return Results.NoContent();
    })
    .WithName("ExternalProgress")
    .AllowAnonymous();
    // ─── /api/mpv-tracker.lua — installable mpv script with baked-in URL ────
    // User downloads this once, drops into mpv's scripts/ dir. Reports playback
    // position to /api/watch/external-progress every 5 seconds, plus on end-of-
    // file so the final position is captured even when mpv is closed abruptly.
    app.MapGet("/api/mpv-tracker.lua", (HttpContext http, DlnaService dlna) =>
    {
        // Use the same advertised origin as DLNA — that's the URL the user
        // already knows reaches Animarr from their LAN.
        var animarrUrl = dlna.AdvertisedHost ?? $"http://{http.Request.Host}";
        var lua = MpvTrackerScript.Build(animarrUrl);
        http.Response.Headers["Content-Disposition"] = "attachment; filename=\"animarr-tracker.lua\"";
        return Results.Text(lua, "text/x-lua; charset=utf-8");
    })
    .WithName("MpvTrackerScript")
    .AllowAnonymous();

        return app;
    }
}

/// <summary>POST body for /api/watch/external-progress (sent by mpv lua script).</summary>
public record ExternalProgressRequest(
    string? Path,
    double PositionSec,
    double? DurationSec,
    int? PlayedDeltaSec);
