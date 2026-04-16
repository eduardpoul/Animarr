using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services;

/// <summary>
/// Background service that polls RenameQueue and processes pending rename jobs.
/// Survives restarts: jobs written to DB before processing starts, recovered on startup.
/// </summary>
public class RenameQueueProcessorService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<AppSettings> appOptions,
    FolderWatcherService watcherService,
    ILogger<RenameQueueProcessorService> logger) : BackgroundService
{
    private readonly int _delayMs = appOptions.Value.WatcherDelayMs;
    private const int PollMs    = 1000;
    private const int MaxRetries = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobsAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessNextBatchAsync(stoppingToken);
        }
    }

    /// <summary>Reset stuck Processing jobs to Queued so they are retried after a restart.</summary>
    private async Task RecoverInterruptedJobsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var count = await db.RenameQueues
            .Where(q => q.Status == RenameQueueStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, RenameQueueStatus.Queued)
                .SetProperty(q => q.ErrorMessage, "Recovered after restart"));
        if (count > 0)
            logger.LogInformation("Recovered {Count} interrupted rename queue jobs.", count);
    }

    private async Task ProcessNextBatchAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Only take jobs that were queued at least _delayMs ago (file had time to be written)
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_delayMs);
        var jobs = await db.RenameQueues
            .Where(q => q.Status == RenameQueueStatus.Queued && q.QueuedAt <= cutoff)
            .OrderBy(q => q.QueuedAt)
            .Take(10)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            // Mark as Processing atomically before doing any work
            job.Status = RenameQueueStatus.Processing;
            await db.SaveChangesAsync(ct);

            try
            {
                if (!File.Exists(job.FilePath))
                {
                    logger.LogDebug("Queue: file no longer exists, skipping: {Path}", job.FilePath);
                    job.Status = RenameQueueStatus.Done;
                    job.ErrorMessage = "File no longer exists";
                    job.ProcessedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var renameService = scope.ServiceProvider.GetRequiredService<IRenameService>();
                await renameService.ProcessSingleFileAsync(job.FilePath, job.FolderId, ct);

                // Check what name the file got (read from RenameHistory)
                var renamed = await db.RenameHistories
                    .Where(h => h.FolderId == job.FolderId && h.OriginalPath == job.FilePath
                             && h.Status == RenameStatus.Renamed)
                    .OrderByDescending(h => h.ProcessedAt)
                    .FirstOrDefaultAsync(ct);

                var newName = renamed is not null
                    ? Path.GetFileName(renamed.NewPath)
                    : Path.GetFileName(job.FilePath);

                // H-5: suppress the FSW Renamed event that File.Move triggers for the new path
                if (renamed is not null)
                    watcherService.SuppressPath(renamed.NewPath);

                watcherService.NotifyFileRenamed(
                    job.FolderId,
                    Path.GetFileName(job.FilePath),
                    newName);

                job.Status = RenameQueueStatus.Done;
                job.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process rename job {Id} for {Path}", job.Id, job.FilePath);
                job.RetryCount++;
                job.ErrorMessage = ex.Message;
                if (job.RetryCount >= MaxRetries)
                {
                    job.Status = RenameQueueStatus.Error;
                    job.ProcessedAt = DateTime.UtcNow;
                    logger.LogWarning("Rename job {Id} failed permanently after {Retries} retries: {Path}",
                        job.Id, job.RetryCount, job.FilePath);
                }
                else
                {
                    job.Status = RenameQueueStatus.Queued;
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
