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

    // ── Live counters & events ──────────────────────────────────────────────
    // Subscribed by the sidebar LLM status card, Settings → AI/LLM hero card,
    // and the Catalog NeedsReview chip. In-process events replace the SignalR
    // hub channels described in the design contract — same push semantics, no
    // protocol overhead.

    public int  QueueDepth { get; private set; }
    public int  QueueProcessedSinceStart { get; private set; }
    public Guid? CurrentlyProcessingFolderId { get; private set; }

    /// <summary>When true the processor keeps polling/refreshing depth but does NOT
    /// start new jobs — the AI-status popup's Pause button flips this. In-memory only:
    /// a process restart resumes (and RecoverInterruptedJobsAsync re-queues). A job
    /// already mid-flight is allowed to finish; pause only blocks picking up the next.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Pause/resume the queue. Fires <see cref="QueueChanged"/> so the hub
    /// broadcasts the new state to the AI popup.</summary>
    public void SetPaused(bool paused)
    {
        if (IsPaused == paused) return;
        IsPaused = paused;
        QueueChanged?.Invoke();
    }

    /// <summary>Count of items the pipeline finished as Identified | Manual since process start.</summary>
    public int HitCount  { get; private set; }
    /// <summary>Count of items that ended up NeedsReview | Failed since process start.</summary>
    public int MissCount { get; private set; }
    /// <summary>HitCount / (HitCount + MissCount). Returns 0 with no samples.</summary>
    public double HitRate => (HitCount + MissCount) == 0
        ? 0
        : (double)HitCount / (HitCount + MissCount);

    /// <summary>Fires when QueueDepth or CurrentlyProcessingFolderId changes — sidebar/AI panel re-render.</summary>
    public event Action? QueueChanged;

    /// <summary>Fires when an item finishes identification (success or failure).
    /// Argument is the folder id whose status changed. Catalog/NeedsReview/MediaDetail react.</summary>
    public event Action<Guid>? ItemIdentified;

    private async Task RefreshQueueDepthAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var newDepth = await db.IdentificationQueues
            .CountAsync(q => q.Status == IdentificationQueueStatus.Queued
                          || q.Status == IdentificationQueueStatus.Processing, ct);
        if (newDepth != QueueDepth)
        {
            QueueDepth = newDepth;
            QueueChanged?.Invoke();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobsAsync();
        await RefreshQueueDepthAsync(stoppingToken);
        _ = WarmUpOllamaAsync(stoppingToken);   // fire-and-forget warm-up

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Paused → don't pick up the next job, but keep refreshing depth so the
            // popup still reflects newly-queued items piling up behind the pause.
            if (!IsPaused)
                await ProcessNextJobAsync(stoppingToken);
            await RefreshQueueDepthAsync(stoppingToken);
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

            // The embedded provider starts the in-container llama-server on demand
            // and idle-stops when not in use — don't probe here, or we'd spin it up
            // (loading the model into RAM/VRAM) at boot for nothing. The first real
            // job lazily starts it.
            var provider = await appCfg.GetAsync(AppConfigKeys.LlmProvider, "compatible") ?? "compatible";
            if (provider == "embedded")
            {
                logger.LogInformation("LLM warm-up: embedded provider — starts on demand.");
                return;
            }

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

        CurrentlyProcessingFolderId = job.FolderId;
        QueueChanged?.Invoke();

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
            LlmIdentifyResult? llmResult = null;

            var ollamaEnabled = await appCfg.GetAsync<bool>(AppConfigKeys.LlmEnabled, false, ct);
            if (ollamaEnabled)
            {
                await using var dbFolder = await dbFactory.CreateDbContextAsync(ct);
                var folder = await dbFolder.FolderWatchers.FindAsync([job.FolderId], ct);
                if (folder is not null)
                {
                    // For flat single-file entries, identify against the file path itself —
                    // the section's directory name (e.g. "Movies") is generic and only
                    // confuses the LLM (e.g. "Movies" + "Home Video" inside a filename →
                    // misidentified as "Home Movies").
                    var pathForLlm = folder.SingleFilePath ?? folder.Path;

                    // Pass the parent section's user-facing label as a category hint
                    // (e.g. "Anime" → expect pinyin/romaji titles, ask for canonical
                    // English translation).
                    string? sectionLabel = null;
                    if (folder.ParentSectionId.HasValue)
                    {
                        var parent = await dbFolder.FolderWatchers.FindAsync(
                            new object[] { folder.ParentSectionId.Value }, ct);
                        sectionLabel = parent?.Label;
                    }

                    Log($"[LLM] Calling LLM for: {pathForLlm} (section: {sectionLabel ?? "<none>"})");
                    llmResult = await llm.IdentifyFolderAsync(pathForLlm, sectionLabel, ct);
                    if (llmResult != null && llmResult.Confidence >= 0.5)
                    {
                        var engPart = !string.IsNullOrWhiteSpace(llmResult.EnglishTitle) &&
                                      !string.Equals(llmResult.EnglishTitle, llmResult.Title, StringComparison.OrdinalIgnoreCase)
                            ? $" english_title=\"{llmResult.EnglishTitle}\""
                            : "";
                        Log($"[LLM] Result: \"{llmResult.Title}\"{engPart} type={llmResult.Type} year={llmResult.Year} confidence={llmResult.Confidence:F2}");
                        logger.LogInformation(
                            "LLM identified '{Path}' as '{Title}' [{Type}] (confidence={Conf:F2})",
                            folder.Path, llmResult.Title, llmResult.Type, llmResult.Confidence);
                    }
                    else if (llmResult != null)
                    {
                        Log($"[LLM] Low confidence ({llmResult.Confidence:F2}) for \"{llmResult.Title}\" — ignored.");
                        llmResult = null;
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

            // Step 2: TMDB / MAL identification + image download (uses LLM type/year hints)
            await metadata.IdentifyFolderAsync(
                job.FolderId,
                llmResult,
                job.ForceRefresh,
                Log,
                ct);

            // Step 3: category classification (best-effort; failures don't block).
            // Resolve the MediaItem owning this folder and run the classifier.
            // Manual overrides are preserved by ClassifyAsync — re-identifications
            // never trample user category edits.
            try
            {
                await using var catDb = await dbFactory.CreateDbContextAsync(ct);
                var itemId = await catDb.MediaItems
                    .Where(m => m.FolderId == job.FolderId)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
                if (itemId is Guid mid)
                {
                    var classifier = scope.ServiceProvider
                        .GetRequiredService<CategoryClassifierService>();
                    var picked = await classifier.ClassifyAsync(mid, ct);
                    if (picked.Count > 0)
                        Log($"[Categories] Classified into {picked.Count} categor{(picked.Count == 1 ? "y" : "ies")}.");
                    else
                        Log("[Categories] No categories matched.");
                }
            }
            catch (Exception catEx)
            {
                logger.LogWarning(catEx, "Category classification failed for folder {FolderId} — continuing.", job.FolderId);
                Log($"[Categories] Classification error: {catEx.Message}");
            }

            // Step 4: optional LLM episode mapping (Tier 1) — only when the user
            // opted in via llm.episode_mapping. Best-effort; failures never block
            // identification, and files the LLM skips just stay deterministic.
            try
            {
                var mapEpisodes = await appCfg.GetAsync<bool>(AppConfigKeys.LlmEpisodeMapping, false, ct);
                if (mapEpisodes)
                {
                    await using var epDb = await dbFactory.CreateDbContextAsync(ct);
                    var epItemId = await epDb.MediaItems
                        .Where(m => m.FolderId == job.FolderId)
                        .Select(m => (Guid?)m.Id)
                        .FirstOrDefaultAsync(ct);
                    if (epItemId is Guid emid)
                    {
                        var epResolver = scope.ServiceProvider.GetRequiredService<EpisodeLlmResolver>();
                        var placed = await epResolver.ResolveAsync(emid, ct);
                        if (placed > 0) Log($"[LLM] Mapped {placed} file(s) to episodes.");
                    }
                }
            }
            catch (Exception epEx)
            {
                logger.LogWarning(epEx, "LLM episode mapping failed for folder {FolderId} — continuing.", job.FolderId);
                Log($"[LLM] Episode mapping error: {epEx.Message}");
            }

            // Step 5: per-season absolute offsets (donghua where TMDB lists the
            // whole run as one season but the disk is split). Deterministic +
            // keyless + self-no-op when not applicable, so it runs always. Best-
            // effort; failures never block identification.
            try
            {
                await using var soDb = await dbFactory.CreateDbContextAsync(ct);
                var soItemId = await soDb.MediaItems
                    .Where(m => m.FolderId == job.FolderId)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
                if (soItemId is Guid smid)
                {
                    var offsetResolver = scope.ServiceProvider.GetRequiredService<SeasonOffsetResolver>();
                    var offsets = await offsetResolver.ResolveAsync(smid, ct);
                    if (offsets is { Count: > 0 })
                        Log($"[Seasons] Computed absolute offsets for {offsets.Count} season(s).");
                }
            }
            catch (Exception soEx)
            {
                logger.LogWarning(soEx, "Season-offset resolve failed for folder {FolderId} — continuing.", job.FolderId);
            }

            job.Status      = IdentificationQueueStatus.Done;
            job.ProcessedAt = DateTime.UtcNow;
            job.ErrorMessage = null;
            job.LogDetails   = logSb.ToString();

            logger.LogInformation("Identification done for folder {FolderId}", job.FolderId);
        }
        catch (TmdbAuthException ex)
        {
            // H-9: auth errors are not retryable — fail immediately so the user sees the cause.
            job.ErrorMessage = ex.Message;
            job.Status       = IdentificationQueueStatus.Failed;
            job.ProcessedAt  = DateTime.UtcNow;
            logger.LogError(ex, "Identification failed (auth) for folder {FolderId}", job.FolderId);
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

        // Update hit-rate from the final MediaItem state.
        await using (var statDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var status = await statDb.MediaItems
                .Where(m => m.FolderId == job.FolderId)
                .Select(m => (IdentificationStatus?)m.IdentificationStatus)
                .FirstOrDefaultAsync(ct);
            if (status is IdentificationStatus.Identified or IdentificationStatus.Manual)
                HitCount++;
            else if (status is IdentificationStatus.NeedsReview or IdentificationStatus.Failed)
                MissCount++;
        }

        // Notify subscribers that this folder's identification finished (success OR failure).
        // Catalog/NeedsReview/MediaDetail subscribers re-query.
        CurrentlyProcessingFolderId = null;
        QueueProcessedSinceStart++;
        ItemIdentified?.Invoke(job.FolderId);
        QueueChanged?.Invoke();
    }
}
