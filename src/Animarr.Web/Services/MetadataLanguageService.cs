using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Runs the "metadata language changed → re-fetch the library's texts/posters" pass in
/// the background and exposes live progress for the Settings UI to poll.
///
/// Singleton; scoped services (DbContext, MetadataService) are resolved per run via
/// <see cref="IServiceScopeFactory"/>, like <see cref="EmbeddedLlamaService"/>. Only one
/// pass runs at a time — a request while one is in flight is ignored. The work is
/// "text only": <see cref="MetadataService.RelocalizeItemAsync"/> rewrites
/// Title/Description/Genres (+ a localized poster when available) for every identified
/// item that has a TMDB id; MAL-only anime are skipped and excluded from the total.
/// </summary>
public sealed class MetadataLanguageService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataLanguageService> logger)
{
    // Permits exactly one background pass at a time. Acquired in StartAsync,
    // released in RunAsync's finally.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile int     _total;
    private volatile int     _done;
    private volatile string  _phase = "idle";   // idle | running | done | error
    private volatile string? _error;
    private volatile string  _language = "en";

    /// <summary>Snapshot of the current/last pass for the Settings progress indicator.</summary>
    public (string Language, int Total, int Done, string Phase, string? Error) Status
        => (_language, _total, _done, _phase, _error);

    /// <summary>
    /// Persists <paramref name="language"/> as the new metadata language and, if the
    /// library has any TMDB-identified items, kicks off the background re-fetch. The
    /// config is written first, so a fresh install that changes the language before
    /// scanning anything still gets new identifications in that language. Returns false
    /// (no-op) if a pass is already running.
    /// </summary>
    public async Task<bool> StartAsync(string language, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;   // already running

        try
        {
            _language = language;
            _error    = null;
            _done     = 0;
            _total    = 0;
            _phase    = "running";

            // Persist the setting first — covers the fresh-install case where there is
            // nothing to re-localise yet but future identifications must use the language.
            using var scope = scopeFactory.CreateScope();
            var cfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
            await cfg.SetAsync(AppConfigKeys.MetadataLanguage, language, ct);
        }
        catch (Exception ex)
        {
            _phase = "error";
            _error = ex.Message;
            _gate.Release();
            logger.LogWarning(ex, "Failed to start metadata re-localisation");
            return false;
        }

        // Run the library pass detached; RunAsync releases the gate when finished.
        _ = Task.Run(() => RunAsync(language), CancellationToken.None);
        return true;
    }

    private async Task RunAsync(string language)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp        = scope.ServiceProvider;
            var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var metadata  = sp.GetRequiredService<MetadataService>();

            await using var db = await dbFactory.CreateDbContextAsync();
            var items = await db.MediaItems
                .Where(m => m.IdentificationStatus == IdentificationStatus.Identified && m.TmdbId != null)
                .ToListAsync();

            _total = items.Count;
            logger.LogInformation("Metadata re-localisation → {Lang}: {Count} TMDB titles", language, _total);

            foreach (var item in items)
            {
                try
                {
                    if (await metadata.RelocalizeItemAsync(item, language))
                    {
                        await db.SaveChangesAsync();
                        metadata.NotifyMediaItemChanged(item.FolderId);
                    }
                }
                catch (TmdbAuthException ex)
                {
                    // Missing/invalid TMDB key — no point hammering the rest.
                    _error = ex.Message;
                    _phase = "error";
                    logger.LogWarning(ex, "Metadata re-localisation aborted — TMDB auth");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Re-localise failed for \"{Title}\" ({Id})", item.Title, item.Id);
                }
                _done++;
            }

            _phase = "done";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _phase = "error";
            logger.LogWarning(ex, "Metadata re-localisation pass failed");
        }
        finally
        {
            _gate.Release();
        }
    }
}
