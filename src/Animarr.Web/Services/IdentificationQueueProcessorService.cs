using System.Text;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services.Segments;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Background service that drains IdentificationQueue and processes jobs one at a time.
/// Serial processing prevents flooding Ollama with parallel LLM requests.
/// Pipeline: LLM (optional) → TMDB/MAL search → download images → save MediaItem.
///
/// Event-driven: enqueue sites (and resume) call <see cref="Wake"/>; while jobs
/// are queued they run back to back (one start per <see cref="JobSpacing"/>,
/// the old poll's pace); an idle queue is only re-checked every
/// <see cref="IdlePoll"/> as a safety net. It used to query SQLite every 2 s
/// forever (~86k queries/day with nothing to do).
/// </summary>
public class IdentificationQueueProcessorService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<IdentificationQueueProcessorService> logger) : BackgroundService
{
    /// <summary>Minimum spacing between job starts — the pace the old 2 s
    /// PeriodicTimer poll imposed (a soft brake on TMDB/LLM request bursts).</summary>
    private static readonly TimeSpan JobSpacing = TimeSpan.FromSeconds(2);
    /// <summary>Fallback re-check of an idle queue when nobody called <see cref="Wake"/>.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 3;

    // Retention: Done/Failed rows (each carrying its multi-KB LogDetails) were
    // never deleted. Rows older than RetentionAge go, except the newest
    // RetentionKeepNewest finished ones — the AI popup reads the latest 200 rows
    // (GET /api/identification-queue), so its list and counters don't change.
    private static readonly TimeSpan RetentionAge   = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetentionEvery = TimeSpan.FromDays(1);
    private const int RetentionKeepNewest = 200;

    private static readonly SemaphoreSlim WakeSignal = new(0);
    private static int _wakePending;

    /// <summary>Nudge the processor: a job was enqueued / removed, or the queue
    /// resumed. Static so enqueue sites that only hold a DbContext (the folder
    /// watcher, folder/media/rename endpoints) need no DI plumbing — there is
    /// one processor per process. Cheap and idempotent: a burst of calls
    /// collapses into one wake-up.</summary>
    public static void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            WakeSignal.Release();
    }

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
        if (!paused) Wake();   // pick the backlog up now, not at the next idle re-check
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

        // First pass one spacing after start (as the old timer's first tick):
        // drains whatever was queued / recovered across the restart.
        try { await Task.Delay(JobSpacing, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var lastPruneAt = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var passStart = DateTime.UtcNow;
            var processed = false;
            try
            {
                if (passStart - lastPruneAt >= RetentionEvery)
                {
                    lastPruneAt = passStart;
                    await PruneFinishedJobsAsync(stoppingToken);
                }

                // Paused → don't pick up the next job, but keep refreshing depth so the
                // popup still reflects newly-queued items piling up behind the pause.
                if (!IsPaused)
                    processed = await ProcessNextJobAsync(stoppingToken);
                await RefreshQueueDepthAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // An exception escaping ExecuteAsync faults the BackgroundService,
                // and the host's default (StopHost) then shuts the WHOLE app down.
                // Reachable: delete a folder (or let the orphan cleanup drop it)
                // while its job is Processing — the queue row cascades away and
                // the job's final SaveChanges throws DbUpdateConcurrencyException.
                logger.LogError(ex, "Identification queue tick failed — continuing.");
                if (CurrentlyProcessingFolderId is not null)
                {
                    CurrentlyProcessingFolderId = null;
                    QueueChanged?.Invoke();
                }
                // A failure may leave work Queued (e.g. for retry) — come back at
                // the old poll pace instead of parking until the idle re-check.
                processed = true;
            }

            try
            {
                if (processed)
                {
                    // More may be waiting: next pass right away, but no sooner
                    // than one JobSpacing after this one started.
                    var wait = JobSpacing - (DateTime.UtcNow - passStart);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
                }
                else
                {
                    // Queue empty (or paused): sleep until Wake() or the idle re-check.
                    await WakeSignal.WaitAsync(IdlePoll, stoppingToken);
                    // Reset AFTER waking: a Wake() from here on — e.g. an enqueue
                    // racing the next pass's query — releases the signal again.
                    Volatile.Write(ref _wakePending, 0);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Delete Done/Failed jobs older than <see cref="RetentionAge"/>,
    /// keeping the newest <see cref="RetentionKeepNewest"/> finished ones
    /// regardless of age. Runs on the first pass and then daily; best-effort.</summary>
    private async Task PruneFinishedJobsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTime.UtcNow - RetentionAge;
            var keep = db.IdentificationQueues
                .Where(q => q.Status == IdentificationQueueStatus.Done
                         || q.Status == IdentificationQueueStatus.Failed)
                .OrderByDescending(q => q.QueuedAt)
                .Take(RetentionKeepNewest)
                .Select(q => q.Id);
            var pruned = await db.IdentificationQueues
                .Where(q => (q.Status == IdentificationQueueStatus.Done
                          || q.Status == IdentificationQueueStatus.Failed)
                         && q.QueuedAt < cutoff
                         && !keep.Contains(q.Id))
                .ExecuteDeleteAsync(ct);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} finished identification jobs older than {Days} days.",
                    pruned, (int)RetentionAge.TotalDays);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pruning finished identification jobs failed — continuing.");
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

    /// <summary>Runs the oldest Queued job. False when there was none.</summary>
    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var job = await db.IdentificationQueues
            .Where(q => q.Status == IdentificationQueueStatus.Queued)
            .OrderBy(q => q.QueuedAt)
            .FirstOrDefaultAsync(ct);

        if (job is null) return false;

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

            // Steps 3-6 all work on the MediaItem step 2 created / updated for
            // this folder — resolve its id once (it used to be looked up again,
            // with a fresh context, in every step). Best-effort like the steps.
            Guid? itemId = null;
            try
            {
                await using var itemDb = await dbFactory.CreateDbContextAsync(ct);
                itemId = await itemDb.MediaItems
                    .Where(m => m.FolderId == job.FolderId)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
            }
            catch (Exception idEx)
            {
                logger.LogWarning(idEx, "MediaItem lookup failed for folder {FolderId} — skipping the post-identify steps.", job.FolderId);
            }

            // Step 3: category classification (best-effort; failures don't block).
            // Run the classifier on the MediaItem owning this folder.
            // Manual overrides are preserved by ClassifyAsync — re-identifications
            // never trample user category edits.
            try
            {
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
                    if (itemId is Guid emid)
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
                if (itemId is Guid smid)
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

            // Step 6: skip-intro/credits detection. Cheap providers only here
            // (AniSkip network lookups by MAL id) — heavy providers (chromaprint)
            // run in the dedicated background pass. Fire-and-forget so a big
            // season's lookups don't stall the queue; the work is idempotent and
            // self-skipping, so a restart that drops the task just re-detects.
            try
            {
                if (itemId is Guid segMid)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var segScope = scopeFactory.CreateScope();
                            var detector = segScope.ServiceProvider.GetRequiredService<SegmentDetectionService>();
                            await detector.DetectForItemAsync(segMid, cheapOnly: true, CancellationToken.None);
                        }
                        catch (Exception bgEx)
                        {
                            logger.LogWarning(bgEx, "[Segments] background detection failed for {Id}", segMid);
                        }
                    }, CancellationToken.None);
                }
            }
            catch (Exception segEx)
            {
                logger.LogWarning(segEx, "[Segments] detection enqueue failed for folder {FolderId} — continuing.", job.FolderId);
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
        return true;
    }
}
