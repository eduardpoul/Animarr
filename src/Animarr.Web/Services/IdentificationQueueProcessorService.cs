using System.Text;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Background service that polls IdentificationQueue and processes jobs one at a time.
/// Serial processing prevents flooding Ollama with parallel LLM requests.
/// Pipeline: LLM (optional) → TMDB/MAL search → download images → save MediaItem.
/// </summary>
public class IdentificationQueueProcessorService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<IdentificationQueueProcessorService> logger) : BackgroundService
{
    private const int PollMs    = 2000;
    private const int MaxRetries = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobsAsync();
        _ = WarmUpOllamaAsync(stoppingToken);   // fire-and-forget warm-up

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessNextJobAsync(stoppingToken);
        }
    }

    private async Task WarmUpOllamaAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var appCfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
            var enabled = await appCfg.GetAsync<bool>(AppConfigKeys.LlmEnabled, false);
            if (!enabled) return;

            var llm = scope.ServiceProvider.GetRequiredService<ILlmService>();
            var available = await llm.IsAvailableAsync();
            if (available)
                logger.LogInformation("LLM warm-up: service is available.");
            else
                logger.LogWarning("LLM warm-up: service is NOT available — check LLM settings.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama warm-up check failed.");
        }
    }

    private async Task RecoverInterruptedJobsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var count = await db.IdentificationQueues
            .Where(q => q.Status == IdentificationQueueStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, IdentificationQueueStatus.Queued)
                .SetProperty(q => q.ErrorMessage, "Recovered after restart"));
        if (count > 0)
            logger.LogInformation("Recovered {Count} interrupted identification jobs.", count);
    }

    private async Task ProcessNextJobAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var job = await db.IdentificationQueues
            .Where(q => q.Status == IdentificationQueueStatus.Queued)
            .OrderBy(q => q.QueuedAt)
            .FirstOrDefaultAsync(ct);

        if (job is null) return;

        job.Status = IdentificationQueueStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var llm      = scope.ServiceProvider.GetRequiredService<ILlmService>();
            var metadata = scope.ServiceProvider.GetRequiredService<MetadataService>();
            var appCfg   = scope.ServiceProvider.GetRequiredService<IAppConfigService>();

            var logSb = new StringBuilder();
            void Log(string line)
            {
                var ts = DateTime.UtcNow.ToString("HH:mm:ss");
                logSb.AppendLine($"[{ts}] {line}");
                logger.LogDebug("[ScanLog] {Line}", line);
            }

            // Step 1: LLM identification (optional, if enabled)
            string? llmTitle     = null;
            double? llmConfidence = null;

            var ollamaEnabled = await appCfg.GetAsync<bool>(AppConfigKeys.LlmEnabled, false, ct);
            if (ollamaEnabled)
            {
                await using var dbFolder = await dbFactory.CreateDbContextAsync(ct);
                var folder = await dbFolder.FolderWatchers.FindAsync([job.FolderId], ct);
                if (folder is not null)
                {
                    Log($"[LLM] Calling LLM for: {folder.Path}");
                    var llmResult = await llm.IdentifyFolderAsync(folder.Path, ct);
                    if (llmResult != null && llmResult.Confidence >= 0.5)
                    {
                        llmTitle      = llmResult.Title;
                        llmConfidence = llmResult.Confidence;
                        Log($"[LLM] Result: \"{llmTitle}\" confidence={llmConfidence:F2}");
                        logger.LogInformation(
                            "LLM identified '{Path}' as '{Title}' (confidence={Conf:F2})",
                            folder.Path, llmTitle, llmConfidence);
                    }
                    else if (llmResult != null)
                    {
                        Log($"[LLM] Low confidence ({llmResult.Confidence:F2}) for \"{llmResult.Title}\" — ignored.");
                    }
                    else
                    {
                        Log("[LLM] No result returned.");
                    }
                }
            }
            else
            {
                Log("[LLM] Disabled in settings.");
            }

            // Step 2: TMDB / MAL identification + image download
            await metadata.IdentifyFolderAsync(
                job.FolderId,
                llmTitle,
                llmConfidence,
                job.ForceRefresh,
                Log,
                ct);

            job.Status      = IdentificationQueueStatus.Done;
            job.ProcessedAt = DateTime.UtcNow;
            job.ErrorMessage = null;
            job.LogDetails   = logSb.ToString();

            logger.LogInformation("Identification done for folder {FolderId}", job.FolderId);
        }
        catch (Exception ex)
        {
            job.RetryCount++;
            job.ErrorMessage = ex.Message;

            if (job.RetryCount >= MaxRetries)
            {
                job.Status      = IdentificationQueueStatus.Failed;
                job.ProcessedAt = DateTime.UtcNow;
                logger.LogError(ex, "Identification failed permanently for folder {FolderId}", job.FolderId);
            }
            else
            {
                job.Status = IdentificationQueueStatus.Queued;
                logger.LogWarning(ex,
                    "Identification failed for folder {FolderId}, retry {Retry}/{Max}",
                    job.FolderId, job.RetryCount, MaxRetries);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
