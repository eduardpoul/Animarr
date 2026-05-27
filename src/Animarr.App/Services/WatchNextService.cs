#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Microsoft.Extensions.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Animarr.App.Services;

/// <summary>
/// Bridges Animarr's per-user playback state to the **Android TV Watch Next**
/// programs feed — the carousel-row that appears on Google TV's home screen
/// labelled "Continue watching" (or "Play Next" on older Leanback launchers).
///
/// Architecture
/// ────────────
/// • One row per active in-progress title in the system's
///   <c>content://android.media.tv/watch_next_program</c> table (mirrors
///   <c>TvContract.WatchNextPrograms</c>). Rows are identified by
///   INTERNAL_PROVIDER_ID = our internal media GUID so we can find &amp;
///   update the same card on every progress tick.
/// • On Upsert: insert-or-update with the latest playback position +
///   last engagement timestamp. The launcher uses last_engagement_time as
///   primary sort, so a fresh tick floats our card up to the front of
///   "Continue watching".
/// • On Remove: drop the row (called when the user marks a title fully
///   watched, or hits delete).
///
/// Done via raw <see cref="ContentResolver"/> calls against the
/// <c>content://android.media.tv/watch_next_program</c> URI rather than the
/// AndroidX.TvProvider NuGet — we only need a handful of column constants,
/// none of which have shifted across API levels, so the binding wrapper is
/// pure overhead.
///
/// Cross-platform: on non-Android targets (Windows / iOS / Mac Catalyst) this
/// service is a no-op shell. The shared JS hook can fire-and-forget without
/// knowing which platform it's running on.
/// </summary>
public sealed class WatchNextService
{
    /// <summary>
    /// Static accessor wired up from MauiProgram after the host is built —
    /// mirrors the pattern used by <see cref="MdnsBrowserService"/> because
    /// the JS bridge needs to dispatch to a singleton without going through
    /// the DI container (JS doesn't see the IServiceProvider).
    /// </summary>
    public static WatchNextService? Instance { get; private set; }

    public static void RegisterStaticInstance(WatchNextService svc) => Instance = svc;

    private readonly ILogger<WatchNextService> _logger;

    public WatchNextService(ILogger<WatchNextService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Insert or update the Watch Next entry for <paramref name="mediaId"/>.
    /// </summary>
    /// <param name="mediaId">Animarr's internal media item ID (GUID stringified).
    ///   Stored in INTERNAL_PROVIDER_ID so we can find &amp; update without
    ///   leaking row IDs into the Blazor side.</param>
    /// <param name="title">Human-readable title shown on the card.</param>
    /// <param name="posterUrl">Absolute URL to the poster image — must be
    ///   reachable from the device (LAN IP works; CDN works; local file://
    ///   would not).</param>
    /// <param name="positionMs">Current playback position. <c>0</c> means
    ///   "not started yet" → the card shows as WATCH_NEXT_TYPE_NEW; anything
    ///   &gt; 0 → WATCH_NEXT_TYPE_CONTINUE with a progress sliver.</param>
    /// <param name="durationMs">Total duration in milliseconds. The launcher
    ///   draws the progress bar as positionMs/durationMs.</param>
    public Task UpsertAsync(string mediaId, string title, string posterUrl,
                             long positionMs, long durationMs)
    {
#if ANDROID
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            if (ctx is null) return Task.CompletedTask;

            // No-op on non-TV Androids (phone, tablet). Watch Next API is
            // available system-wide but only TV launchers render the row, and
            // pushing programs from a phone would just clutter no-one's UI.
            if (ctx.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) != true)
                return Task.CompletedTask;

            var resolver = ctx.ContentResolver;
            if (resolver is null) return Task.CompletedTask;

            var existingId = FindExistingProgramId(resolver, mediaId);

            using var values = new ContentValues();
            values.Put(ColTitle, title);
            // TYPE_MOVIE — episode-level Watch Next (TYPE_TV_EPISODE) needs
            // linkage to a TYPE_TV_SERIES program in the previews channel,
            // which is a separate feature. Treating everything as a movie
            // gives a single card per Animarr title with the right resume
            // semantics; per-episode resume is still visible inside the app.
            values.Put(ColType, TypeMovie);
            values.Put(ColWatchNextType, positionMs > 0 ? WatchNextTypeContinue : WatchNextTypeNew);
            values.Put(ColPosterUri, posterUrl);
            values.Put(ColLastPlaybackPositionMs, positionMs);
            if (durationMs > 0) values.Put(ColDurationMs, durationMs);
            values.Put(ColIntentUri, $"animarr://play/{mediaId}");
            values.Put(ColInternalProviderId, mediaId);
            // last_engagement_time drives the launcher's sort. Touching it
            // every progress tick keeps the card near the top of the row
            // while the user is actively watching.
            values.Put(ColLastEngagementTimeMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (existingId is long id)
            {
                var updateUri = Android.Net.Uri.WithAppendedPath(WatchNextContentUri, id.ToString());
                resolver.Update(updateUri!, values, null, null);
                _logger.LogDebug("WatchNext updated row {RowId} for media {MediaId}", id, mediaId);
            }
            else
            {
                resolver.Insert(WatchNextContentUri, values);
                _logger.LogDebug("WatchNext inserted new row for media {MediaId}", mediaId);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "WatchNext upsert failed for {MediaId}", mediaId);
        }
#endif
        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove this title's Watch Next card (user marked it fully watched, or
    /// deleted the media from Animarr). Safe to call when no row exists.
    /// </summary>
    public Task RemoveAsync(string mediaId)
    {
#if ANDROID
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            if (ctx is null) return Task.CompletedTask;
            if (ctx.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) != true)
                return Task.CompletedTask;
            var resolver = ctx.ContentResolver;
            if (resolver is null) return Task.CompletedTask;

            var existingId = FindExistingProgramId(resolver, mediaId);
            if (existingId is long id)
            {
                var deleteUri = Android.Net.Uri.WithAppendedPath(WatchNextContentUri, id.ToString());
                resolver.Delete(deleteUri!, null, null);
                _logger.LogDebug("WatchNext removed row {RowId} for media {MediaId}", id, mediaId);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "WatchNext remove failed for {MediaId}", mediaId);
        }
#endif
        return Task.CompletedTask;
    }

#if ANDROID
    // ─── TvProvider URI + column constants ──────────────────────────────
    //
    // These match android.media.tv.TvContract.WatchNextPrograms exactly —
    // android.provider.BaseColumns + TvContract.BaseTvColumns + the
    // watch-next-specific columns. Kept as plain string constants instead of
    // referencing the AndroidX binding so the build doesn't pick up a new
    // dependency for what amounts to seven string literals.
    private static readonly Android.Net.Uri WatchNextContentUri =
        Android.Net.Uri.Parse("content://android.media.tv/watch_next_program")!;

    private const string ColTitle                   = "title";
    private const string ColType                    = "type";
    private const string ColWatchNextType           = "watch_next_type";
    private const string ColPosterUri               = "poster_art_uri";
    private const string ColLastPlaybackPositionMs  = "last_playback_position_millis";
    private const string ColDurationMs              = "duration_millis";
    private const string ColIntentUri               = "intent_uri";
    private const string ColInternalProviderId      = "internal_provider_id";
    private const string ColLastEngagementTimeMs    = "last_engagement_time_utc_millis";

    // TvContract.WatchNextPrograms.TYPE_MOVIE.
    private const int TypeMovie               = 0;

    // TvContract.WatchNextPrograms.WATCH_NEXT_TYPE_*.
    private const int WatchNextTypeContinue   = 0;
    private const int WatchNextTypeNew        = 2;

    /// <summary>
    /// Look up the row ID for the Watch Next program whose
    /// <c>internal_provider_id</c> matches our <paramref name="mediaId"/>.
    /// Returns null when no such row exists.
    /// </summary>
    private static long? FindExistingProgramId(ContentResolver resolver, string mediaId)
    {
        using var cursor = resolver.Query(
            WatchNextContentUri,
            projection: new[] { "_id", ColInternalProviderId },
            selection: null, selectionArgs: null, sortOrder: null);
        if (cursor is null) return null;

        var idColIdx       = cursor.GetColumnIndex("_id");
        var providerColIdx = cursor.GetColumnIndex(ColInternalProviderId);
        if (idColIdx < 0 || providerColIdx < 0) return null;

        while (cursor.MoveToNext())
        {
            // GetString may return null for rows our predecessor apps inserted
            // without an internal_provider_id; skip those rather than NPE'ing.
            var providerId = cursor.GetString(providerColIdx);
            if (string.Equals(providerId, mediaId, System.StringComparison.Ordinal))
                return cursor.GetLong(idColIdx);
        }
        return null;
    }
#endif
}
