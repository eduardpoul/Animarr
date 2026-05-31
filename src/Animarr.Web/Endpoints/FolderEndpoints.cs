using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for managing FolderWatchers — the user's library roots.
/// Mirrors the operations the Settings → Root folders panel performs, so
/// the UI can be moved to the WASM client without losing parity.
/// </summary>
internal static class FolderEndpoints
{
    public static IEndpointRouteBuilder MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Folders, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapGet(ApiRoutes.FolderById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(row.ToDto());
        });

        // NOTE: the watcher service auto-discovers FolderWatcher rows via DB polling
        // / FileSystemWatcher events. Endpoint handlers persist the row and the
        // watcher picks it up on the next scan; we call StartWatcherAsync/StopWatcherAsync
        // for an immediate effect rather than waiting for the poll.
        app.MapPost(ApiRoutes.Folders, async (
            CreateFolderRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest("Path is required.");

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var entity = new FolderWatcher
            {
                Id              = Guid.NewGuid(),
                Path            = request.Path,
                Label           = string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileName(request.Path.TrimEnd('/', '\\')) : request.Label,
                FolderType      = (Animarr.Web.Data.Models.FolderType)(int)request.FolderType,
                WatchEnabled    = request.WatchEnabled,
                IdentifyEnabled = request.IdentifyEnabled,
                IsSection       = request.IsSection,
                FlatSection     = request.FlatSection,
                Hue             = request.Hue,
                BackdropPath    = request.BackdropPath,
                CreatedAt       = DateTime.UtcNow,
            };

            db.FolderWatchers.Add(entity);
            await db.SaveChangesAsync(ct);

            // Spin up the FileSystemWatcher right away so the user doesn't have to
            // wait for the next poll cycle to see new files appear.
            await watcher.StartWatcherAsync(entity.Id);

            return Results.Created(ApiRoutes.Folder(entity.Id), entity.ToDto());
        });

        app.MapPut(ApiRoutes.FolderById, async (
            Guid id,
            UpdateFolderRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            entity.Label           = request.Label;
            entity.FolderType      = (Animarr.Web.Data.Models.FolderType)(int)request.FolderType;
            entity.WatchEnabled    = request.WatchEnabled;
            entity.IdentifyEnabled = request.IdentifyEnabled;
            entity.Hue             = request.Hue;
            entity.BackdropPath    = request.BackdropPath;
            await db.SaveChangesAsync(ct);

            // Toggle the watcher to pick up the new WatchEnabled state immediately.
            if (request.WatchEnabled) await watcher.StartWatcherAsync(id);
            else                      await watcher.StopWatcherAsync(id);
            return Results.Ok(entity.ToDto());
        });

        app.MapDelete(ApiRoutes.FolderById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Cascade to any children of a section we owned.
            if (entity.IsSection)
            {
                var children = await db.FolderWatchers
                    .Where(f => f.ParentSectionId == id)
                    .ToListAsync(ct);
                db.FolderWatchers.RemoveRange(children);
            }
            db.FolderWatchers.Remove(entity);
            await db.SaveChangesAsync(ct);

            await watcher.StopWatcherAsync(id);
            return Results.NoContent();
        });

        app.MapPost(ApiRoutes.FolderScan, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Section roots re-discover children; ordinary folders just restart the watcher.
            if (entity.IsSection)
            {
                // Run discovery in the BACKGROUND and return 202 immediately: a large
                // section registers many child folders + starts a FileSystemWatcher
                // each, which on a slow (FUSE) media mount can exceed the HTTP timeout
                // — the request would appear to fail even though the work completes.
                //
                // Crucially, ENQUEUE the newly-discovered folders for identification.
                // DiscoverChildrenAsync only registers them (it fires SubfolderCreated,
                // which is a UI-only notification); the live FileSystemWatcher Created
                // path is what normally queues identification. So a bulk rescan must
                // queue them itself, otherwise discovered folders sit unidentified.
                _ = Task.Run(async () =>
                {
                    var log = loggerFactory.CreateLogger("FolderScan");
                    try
                    {
                        // 1) Register any new on-disk folders.
                        var newIds = await watcher.DiscoverChildrenAsync(id);

                        // 2) Enqueue identification for every child under this section that
                        //    still has no MediaItem — covers the just-discovered folders AND
                        //    any previously-registered-but-never-identified ones — skipping
                        //    anything already queued/processing. DiscoverChildrenAsync only
                        //    REGISTERS folders (the live FileSystemWatcher Created path is what
                        //    normally enqueues), so without this a rescan finds folders but
                        //    never identifies them.
                        await using var qdb = await dbFactory.CreateDbContextAsync();
                        const Animarr.Web.Data.Models.IdentificationQueueStatus queued =
                            Animarr.Web.Data.Models.IdentificationQueueStatus.Queued;

                        var alreadyQueued = (await qdb.IdentificationQueues
                            .Where(q => q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Queued
                                     || q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Processing)
                            .Select(q => q.FolderId)
                            .ToListAsync()).ToHashSet();

                        var unidentified = await qdb.FolderWatchers
                            .Where(f => f.ParentSectionId == id && !f.IsSection && f.IdentifyEnabled
                                     && !qdb.MediaItems.Any(m => m.FolderId == f.Id))
                            .Select(f => f.Id)
                            .ToListAsync();

                        var toQueue = newIds.Concat(unidentified)
                            .Distinct()
                            .Where(fid => !alreadyQueued.Contains(fid))
                            .ToList();

                        foreach (var fid in toQueue)
                            qdb.IdentificationQueues.Add(new IdentificationQueue
                            {
                                Id       = Guid.NewGuid(),
                                FolderId = fid,
                                Status   = queued,
                                QueuedAt = DateTime.UtcNow,
                            });
                        if (toQueue.Count > 0) await qdb.SaveChangesAsync();
                        log.LogInformation(
                            "Rescan of section {Id}: {New} new folder(s) discovered, {Queued} queued for identification.",
                            id, newIds.Count, toQueue.Count);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Background rescan failed for section {Id}", id);
                    }
                });
            }
            else
            {
                await watcher.StartWatcherAsync(id);
            }
            return Results.Accepted();
        });

        app.MapGet(ApiRoutes.FolderChildren, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .Where(f => f.ParentSectionId == id)
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapGet(ApiRoutes.SectionFolders, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .Where(f => f.IsSection)
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        // Filesystem browser — backs the SectionFolderDialog's drill-down picker.
        // Without a `path` arg, returns the server's well-known roots (/mnt,
        // /Pool-*/*, Windows drive letters). With a `path`, returns its
        // immediate subdirectories. Safe because we only ever expose Directory
        // listings — file content stays gated by the existing /api/image,
        // /api/video, /api/file path-whitelist checks.
        app.MapGet(ApiRoutes.FoldersBrowse, (string? path) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                var roots = DiscoverRoots()
                    .Select(r => new Animarr.Shared.Models.FolderBrowseEntryDto(
                        Path:   r,
                        Name:   System.IO.Path.GetFileName(r.TrimEnd('/', '\\')) is { Length: > 0 } leaf ? leaf : r,
                        IsRoot: true))
                    .ToArray();
                return Results.Ok(roots);
            }

            try
            {
                if (!Directory.Exists(path)) return Results.NotFound();
                var children = Directory.GetDirectories(path)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new Animarr.Shared.Models.FolderBrowseEntryDto(
                        Path:   d,
                        Name:   System.IO.Path.GetFileName(d.TrimEnd('/', '\\')),
                        IsRoot: false))
                    .ToArray();
                return Results.Ok(children);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception) { return Results.Problem("Couldn't read directory."); }
        });

        // v5: multipart upload — drop arbitrary files into a watcher's folder
        // without going through the torrent engine. Third tab of the Add
        // download drawer. Conservative: per-file size capped at 4 GiB,
        // skips entries that would escape the watcher root via "../" in
        // the supplied filename. Returns the number of files actually
        // written so the caller can render an accurate toast.
        app.MapPost(ApiRoutes.FolderUpload, async (
            Guid watcherId,
            HttpRequest httpReq,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (!httpReq.HasFormContentType)
                return Results.BadRequest("multipart/form-data expected.");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var folder = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == watcherId, ct);
            if (folder is null) return Results.NotFound("Unknown folder.");
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
                return Results.Problem("Folder path missing on disk.");

            var form = await httpReq.ReadFormAsync(ct);
            var written = 0;
            foreach (var file in form.Files)
            {
                if (file.Length <= 0) continue;
                // Strip any directory parts from the supplied filename so a
                // malicious "../../../etc/passwd" can't escape the watcher root.
                var safeName = System.IO.Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) continue;
                var target = System.IO.Path.Combine(folder.Path, safeName);
                try
                {
                    await using var fs = System.IO.File.Create(target);
                    await file.CopyToAsync(fs, ct);
                    written++;
                }
                catch
                {
                    // One bad file shouldn't abort the rest of the batch.
                }
            }
            return Results.Ok(new { Written = written });
        }).DisableAntiforgery();

        return app;
    }

    /// <summary>Same root-discovery the Razor SectionFolderDialog used.
    /// Lists /mnt/*, /Pool-*/*, and Windows drive letters.</summary>
    private static List<string> DiscoverRoots()
    {
        var candidates = new List<string>();
        try
        {
            if (Directory.Exists("/mnt"))
                foreach (var d in Directory.GetDirectories("/mnt"))
                    candidates.Add(d);
        }
        catch { /* ignore */ }

        try
        {
            foreach (var pool in Directory.EnumerateDirectories("/")
                .Where(d => System.IO.Path.GetFileName(d).StartsWith("Pool-", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(pool))
                        candidates.Add(d);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                candidates.Add(drive.RootDirectory.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
        catch { /* ignore */ }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
