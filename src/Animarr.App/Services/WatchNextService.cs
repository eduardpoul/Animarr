#if ANDROID
using Android.Content;
using Android.Content.PM;
#endif
using Microsoft.Extensions.Logging;

namespace Animarr.App.Services;

/// <summary>
/// The TV's "Continue watching" cards on the launcher — Android TV's
/// <b>Watch Next</b> row (<c>content://android.media.tv/watch_next_program</c>,
/// <c>TvContract.WatchNextPrograms</c>), which Google TV's home screen shows as
/// "Continue watching" and any launcher reading the system feed (ours too)
/// can show.
///
/// The server's Continue + Next up list is the source of truth:
/// <see cref="SyncAsync"/> makes this app's rows match it — one card per
/// title, keyed <c>{mediaId}</c> (a film) or <c>{mediaId}:{season}:{episode}</c>
/// (internal_provider_id), so the next episode is a new card. A card the user
/// removed from the row stays out until that title moves on to another
/// episode: the launcher hides it from us (gone, or not browsable), so the
/// cards we published are remembered and one that vanished counts as
/// dismissed (Preferences). A tap opens
/// <c>animarr://play/{mediaId}</c>: the TV home resumes it (TvHome.PlayOrOpenAsync).
///
/// The launcher loads the card's picture itself, and our images need the
/// app's media token — so they are cached in the app and served by
/// <c>WatchNextImageProvider</c> (content://, no token in the system feed).
///
/// Raw ContentResolver calls with the column names (stable since API 26), no
/// AndroidX.TvProvider binding. A no-op off Android TV.
/// </summary>
public sealed class WatchNextService
{
    /// <summary>Static accessor wired up from MauiProgram (the JS bridge and the
    /// TV screens reach it without the DI container).</summary>
    public static WatchNextService? Instance { get; private set; }

    public static void RegisterStaticInstance(WatchNextService svc) => Instance = svc;

    private readonly ILogger<WatchNextService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WatchNextService(ILogger<WatchNextService> logger) => _logger = logger;

    /// <summary>One card. <paramref name="ImageUrls"/>: tried in order (an
    /// episode still, then the title's art).</summary>
    public sealed record Card(
        Guid MediaItemId, string Title, int? Season, int? Episode, string? EpisodeTitle,
        bool IsNew, long PositionMs, long DurationMs, DateTime LastEngagementUtc,
        IReadOnlyList<string> ImageUrls);

    /// <summary>At most this many cards — a launcher row, not the library.</summary>
    public const int MaxCards = 10;

    /// <summary>The JS bridge's per-tick upsert, from the web player era: the TV
    /// publishes the server's list now (<see cref="SyncAsync"/>).</summary>
    public Task UpsertAsync(string mediaId, string title, string posterUrl, long positionMs, long durationMs)
        => Task.CompletedTask;

    /// <summary>Drop this title's card (marked watched, deleted).</summary>
    public async Task RemoveAsync(string mediaId)
    {
#if ANDROID
        if (Resolver() is not { } resolver || !Guid.TryParse(mediaId, out var id)) return;
        await _gate.WaitAsync();
        try
        {
            foreach (var row in ReadRows(resolver).Where(r => TitleOf(r.Key) == id))
                resolver.Delete(RowUri(row.Id), null, null);
            var published = Load<Dictionary<string, long>>(PublishedPref) ?? new();
            foreach (var k in published.Keys.Where(k => TitleOf(k) == id).ToList()) published.Remove(k);
            Save(PublishedPref, published);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WatchNext remove failed for {MediaId}", mediaId); }
        finally { _gate.Release(); }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>Make the row match <paramref name="cards"/> (the newest first):
    /// update / add theirs, drop ours that left the list.</summary>
    public async Task SyncAsync(IReadOnlyList<Card> cards, HttpClient http, CancellationToken ct = default)
    {
#if ANDROID
        if (Resolver() is not { } resolver) return;
        var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
        await _gate.WaitAsync(ct);
        try
        {
            var ours = ReadRows(resolver).Where(r => TitleOf(r.Key) is not null).ToList();
            // A card we published that the provider no longer shows us: the
            // user removed it from the row.
            var published = Load<Dictionary<string, long>>(PublishedPref) ?? new();
            var dismissed = new HashSet<string>(Load<string[]>(DismissedPref) ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var (k, rowId) in published)
                if (!ours.Any(r => r.Id == rowId && r.Browsable)) dismissed.Add(k);

            var keep = new HashSet<long>();
            var nowPublished = new Dictionary<string, long>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            var wanted = cards.Take(MaxCards).ToList();
            foreach (var card in wanted)
            {
                var key = KeyOf(card);
                var same = ours.FirstOrDefault(r => r.Key == key);
                // Removed by the user: not back until the title moves on.
                if (dismissed.Contains(key) || same is { Browsable: false })
                {
                    dismissed.Add(key);
                    if (same is not null) keep.Add(same.Id);
                    continue;
                }

                var image = await CacheImageAsync(ctx, http, key, card.ImageUrls, ct);
                if (image is not null) files.Add(image.Value.File);
                using var values = Values(ctx, card, key, image?.Uri);
                if (same is not null)
                {
                    resolver.Update(RowUri(same.Id), values, null, null);
                    keep.Add(same.Id);
                    nowPublished[key] = same.Id;
                }
                else if (resolver.Insert(ContentUri, values) is { } added)
                {
                    var rowId = ContentUris.ParseId(added);
                    keep.Add(rowId);
                    nowPublished[key] = rowId;
                }
            }
            foreach (var row in ours.Where(r => !keep.Contains(r.Id)))
                resolver.Delete(RowUri(row.Id), null, null);
            // A dismissal lasts while the title still wants that same card.
            var wantedKeys = wanted.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            dismissed.RemoveWhere(k => !wantedKeys.Contains(k));
            Save(PublishedPref, nowPublished);
            Save(DismissedPref, dismissed.ToArray());
            PruneImages(ctx, files);
            Android.Util.Log.Info(LogTag, $"{keep.Count} card(s), {files.Count} picture(s); found {ours.Count} row(s) " +
                $"({ours.Count(r => !r.Browsable)} hidden), published {published.Count}, dismissed {dismissed.Count}, wanted {wanted.Count}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Android.Util.Log.Warn(LogTag, $"sync failed: {ex.GetType().Name}: {ex.Message}");
            _logger.LogWarning(ex, "WatchNext sync failed");
        }
        finally { _gate.Release(); }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>Remove every card of this app (signed out: nobody's list stays
    /// on the home screen).</summary>
    public async Task ClearAsync()
    {
#if ANDROID
        if (Resolver() is not { } resolver) return;
        await _gate.WaitAsync();
        try
        {
            foreach (var row in ReadRows(resolver).Where(r => TitleOf(r.Key) is not null))
                resolver.Delete(RowUri(row.Id), null, null);
            Save(PublishedPref, new Dictionary<string, long>());
            Save(DismissedPref, Array.Empty<string>());
            PruneImages(Microsoft.Maui.ApplicationModel.Platform.AppContext, new HashSet<string>());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WatchNext clear failed"); }
        finally { _gate.Release(); }
#else
        await Task.CompletedTask;
#endif
    }

    // What this app published (key → row id) and what the user removed.
    private const string PublishedPref = "animarr_watchnext_published";
    private const string DismissedPref = "animarr_watchnext_dismissed";

    private static T? Load<T>(string pref)
    {
        try
        {
            var json = Microsoft.Maui.Storage.Preferences.Get(pref, "");
            return string.IsNullOrEmpty(json) ? default : System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch { return default; }
    }

    private static void Save<T>(string pref, T value)
    {
        try { Microsoft.Maui.Storage.Preferences.Set(pref, System.Text.Json.JsonSerializer.Serialize(value)); }
        catch { /* the next sync tries again */ }
    }

    private static string KeyOf(Card c) => c.Episode is int e ? $"{c.MediaItemId:D}:{c.Season ?? 0}:{e}" : c.MediaItemId.ToString("D");

    /// <summary>The title of one of our keys; null for anything else.</summary>
    private static Guid? TitleOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var i = key.IndexOf(':');
        return Guid.TryParse(i < 0 ? key : key[..i], out var g) ? g : null;
    }

#if ANDROID
    private const string LogTag = "Animarr.WatchNext";

    private static ContentResolver? Resolver()
    {
        var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
        // Only a TV launcher renders the row; a phone stays out of it.
        return ctx?.PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) == true ? ctx.ContentResolver : null;
    }

    private sealed record Row(long Id, string? Key, bool Browsable);

    /// <summary>This app's rows (the provider shows a caller only its own). No
    /// selection: without ACCESS_ALL_EPG_DATA the provider may refuse one.</summary>
    private static List<Row> ReadRows(ContentResolver resolver)
    {
        var rows = new List<Row>();
        using var c = resolver.Query(ContentUri, new[] { "_id", ColInternalProviderId, ColBrowsable }, null, null, null);
        if (c is null) return rows;
        while (c.MoveToNext())
            rows.Add(new Row(c.GetLong(0), c.IsNull(1) ? null : c.GetString(1), c.IsNull(2) || c.GetInt(2) != 0));
        return rows;
    }

    private static ContentValues Values(Context ctx, Card card, string key, string? imageUri)
    {
        var v = new ContentValues();
        v.Put(ColType, card.Episode is null ? TypeMovie : TypeTvEpisode);
        v.Put(ColWatchNextType, card.IsNew ? WatchNextTypeNew : card.PositionMs > 0 ? WatchNextTypeContinue : WatchNextTypeNext);
        v.Put(ColTitle, card.Title);
        if (card.Episode is int ep)
        {
            if (card.Season is int s) v.Put(ColSeasonNumber, s.ToString(System.Globalization.CultureInfo.InvariantCulture));
            v.Put(ColEpisodeNumber, ep.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(card.EpisodeTitle)) v.Put(ColEpisodeTitle, card.EpisodeTitle);
        }
        if (imageUri is not null)
        {
            v.Put(ColPosterUri, imageUri);
            v.Put(ColPosterAspect, AspectRatio16x9);
        }
        v.Put(ColLastPlaybackPositionMs, card.PositionMs);
        if (card.DurationMs > 0) v.Put(ColDurationMs, card.DurationMs);
        var open = new Intent(Intent.ActionView, Android.Net.Uri.Parse($"animarr://play/{card.MediaItemId:D}"));
        open.SetPackage(ctx.PackageName);
        v.Put(ColIntentUri, open.ToUri(IntentUriType.Scheme));
        v.Put(ColInternalProviderId, key);
        v.Put(ColLastEngagementTimeMs, new DateTimeOffset(DateTime.SpecifyKind(card.LastEngagementUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        return v;
    }

    // ── Pictures (served by WatchNextImageProvider) ──────────────────────────

    internal static string ImageDir(Context ctx) => Path.Combine(ctx.FilesDir!.AbsolutePath, "watchnext");

    /// <summary>The card's picture in the app's files (fetched once per card,
    /// again after a week) and its content:// address; null when none loads.</summary>
    private static async Task<(string File, string Uri)?> CacheImageAsync(Context ctx, HttpClient http, string key,
        IReadOnlyList<string> urls, CancellationToken ct)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var dir = ImageDir(ctx);
        var file = Path.Combine(dir, name);
        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
        {
            Directory.CreateDirectory(dir);
            foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(url, ct);
                    if (WatchNextImages.MimeOf(bytes) is null) continue;
                    var tmp = file + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, file, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* the next one */ }
            }
        }
        return File.Exists(file) ? (name, $"content://{WatchNextImages.Authority}/{name}") : null;
    }

    private static void PruneImages(Context ctx, HashSet<string> keep)
    {
        var dir = ImageDir(ctx);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
            if (!keep.Contains(Path.GetFileName(f)))
                try { File.Delete(f); } catch { }
    }

    // ── TvContract.WatchNextPrograms ─────────────────────────────────────────
    private static readonly Android.Net.Uri ContentUri = Android.Net.Uri.Parse("content://android.media.tv/watch_next_program")!;
    private static Android.Net.Uri RowUri(long id) => ContentUris.WithAppendedId(ContentUri, id)!;

    private const string ColTitle                  = "title";
    private const string ColEpisodeTitle           = "episode_title";
    private const string ColSeasonNumber           = "season_display_number";
    private const string ColEpisodeNumber          = "episode_display_number";
    private const string ColType                   = "type";
    private const string ColWatchNextType          = "watch_next_type";
    private const string ColPosterUri              = "poster_art_uri";
    private const string ColPosterAspect           = "poster_art_aspect_ratio";
    private const string ColLastPlaybackPositionMs = "last_playback_position_millis";
    private const string ColDurationMs             = "duration_millis";
    private const string ColIntentUri              = "intent_uri";
    private const string ColInternalProviderId     = "internal_provider_id";
    private const string ColLastEngagementTimeMs   = "last_engagement_time_utc_millis";
    private const string ColBrowsable              = "browsable";

    private const int TypeMovie       = 0;   // TYPE_MOVIE
    private const int TypeTvEpisode   = 3;   // TYPE_TV_EPISODE
    private const int WatchNextTypeContinue = 0;
    private const int WatchNextTypeNext     = 1;
    private const int WatchNextTypeNew      = 2;
    private const int AspectRatio16x9 = 0;   // ASPECT_RATIO_16_9
#endif
}

/// <summary>Where the cards' pictures are served from (<c>WatchNextImageProvider</c>)
/// and what a cached file is.</summary>
public static class WatchNextImages
{
    public const string Authority = "com.animarr.app.watchnext";

    /// <summary>A cached picture's name: the card key's SHA-1, hex.</summary>
    public static bool IsName(string? name) =>
        name is { Length: 40 } && name.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The picture's type by its first bytes; null when it isn't one.</summary>
    public static string? MimeOf(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
        : null;
}
