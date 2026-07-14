using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

// Anime theme music (OP/ED) resolution + download via AnimeThemes/AniList.
public partial class MetadataService
{
    // ── Theme music (anime OP/ED) ─────────────────────────────────────────────

    /// <summary>
    /// Best-effort fetch of the title's opening/ending theme from AnimeThemes.moe,
    /// cached as theme.ogg in the media folder's <c>.animarr/&lt;folderId&gt;/</c> dir
    /// (NOT the central image cache — kept next to the media to keep the Docker data
    /// volume small). Only runs for anime-like items. Never throws — theme music is
    /// non-critical, so any failure (no match, read-only mount, network) just leaves
    /// ThemePath null and identification continues.
    /// </summary>
    private async Task FillThemeMusicAsync(
        MediaItem item, FolderWatcher folder, bool forceRefresh, Action<string>? log, CancellationToken ct,
        bool bypassEnabledGate = false)
    {
        try
        {
            // Skip the network-heavy prefetch when no user has theme music enabled —
            // keeps identification fast and avoids downloading audio nobody will play.
            // Bypassed for explicit user-triggered refreshes (the drawer's Rescan button).
            if (!bypassEnabledGate)
            {
                await using var prefDb = await dbFactory.CreateDbContextAsync(ct);
                if (!await prefDb.UserPreferences.AnyAsync(p => p.ThemeMusicEnabled, ct))
                    return;
            }

            var genres = DeserialiseGenreNames(item.GenresJson);
            if (!LooksLikeAnime(item, genres)) return;

            var dir = ThemeDir(folder);
            if (dir is null) { log?.Invoke("[Theme] No media dir resolved — skipping."); return; }
            var dest = Path.Combine(dir, "theme.ogg");

            // Idempotent: keep the existing file unless a forced refresh.
            if (!forceRefresh && File.Exists(dest))
            {
                item.ThemePath = dest;
                return;
            }

            // Resolve a MAL/AniList id. MAL is off by default, so most anime reach
            // here without item.MalId — bridge via AniList (title → idMal).
            int? malId = item.MalId;
            int? aniListId = item.AniListId;
            if (malId is null)
            {
                var match = await aniList.ResolveAsync(item.EnglishTitle ?? item.Title, ct);
                if (match is not null)
                {
                    malId     = match.IdMal;
                    aniListId = match.AniListId;
                    log?.Invoke($"[Theme] AniList resolved \"{item.Title}\" → mal={malId} anilist={aniListId} ({match.Title})");
                    // Keep the bridged ids — AniListId in particular feeds the
                    // airing-schedule / relations features. item is tracked by
                    // the caller's context and saved with the theme fields.
                    if (item.MalId is not > 0 && malId is > 0) item.MalId = malId;
                    if (item.AniListId is not > 0 && aniListId is > 0) item.AniListId = aniListId;
                }
            }

            AnimeThemesClient.ThemePick? pick = null;
            if (malId is int m)
                pick = await animeThemes.GetBestThemeAsync("MyAnimeList", m.ToString(), ct);
            if (pick is null && aniListId is int a)
                pick = await animeThemes.GetBestThemeAsync("AniList", a.ToString(), ct);

            if (pick is null) { log?.Invoke($"[Theme] No theme found for \"{item.Title}\"."); return; }

            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { log?.Invoke($"[Theme] Can't create {dir} (read-only media mount?): {ex.Message}"); return; }

            if (await animeThemes.DownloadAsync(pick.AudioUrl, dest, ct))
            {
                item.ThemePath  = dest;
                item.ThemeTitle = pick.Title;
                log?.Invoke($"[Theme] {dest} ✓ ({pick.Title})");
            }
            else
            {
                log?.Invoke($"[Theme] download failed: {pick.AudioUrl}");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Theme music fetch failed for folder {Id}", folder.Id);
        }
    }

    /// <summary>Public on-demand theme fetch for an existing item — the detail page
    /// triggers this (via GET /api/media/{id}/theme) so the current library backfills
    /// lazily without a full re-identify. Returns the cached path, or null when the
    /// title has no theme / isn't anime. Persists ThemePath/ThemeTitle when found.</summary>
    public async Task<string?> EnsureThemeMusicAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return null;
        if (!string.IsNullOrEmpty(item.ThemePath) && File.Exists(item.ThemePath)) return item.ThemePath;

        var folder = await db.FolderWatchers.FindAsync([item.FolderId], ct);
        if (folder is null) return null;

        await FillThemeMusicAsync(item, folder, forceRefresh: false, log: null, ct);
        // Save whatever the pass produced — ThemePath and/or freshly bridged
        // MAL/AniList ids (worth keeping even when no theme was found).
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return item.ThemePath;
    }

    /// <summary>Explicit user-triggered re-fetch (the Edit Metadata drawer's THEME MUSIC
    /// "Rescan" button). Forces a fresh AnimeThemes lookup and bypasses the global
    /// "any user enabled" gate (the user is asking for it directly). Returns the new
    /// ThemePath, or null when nothing was found.</summary>
    public async Task<string?> RefreshThemeMusicAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return null;
        var folder = await db.FolderWatchers.FindAsync([item.FolderId], ct);
        if (folder is null) return null;

        await FillThemeMusicAsync(item, folder, forceRefresh: true, log: null, ct, bypassEnabledGate: true);
        await db.SaveChangesAsync(ct);
        NotifyMediaItemChanged(item.FolderId);
        return item.ThemePath;
    }

    /// <summary>Manual override (the drawer's THEME MUSIC "Add" button): download a
    /// user-supplied direct audio URL and use it as the title's theme. Works for any
    /// title (no anime gate) — the user picked the file. Returns the new path or null.</summary>
    public async Task<string?> SetThemeFromUrlAsync(Guid mediaItemId, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return null;
        var folder = await db.FolderWatchers.FindAsync([item.FolderId], ct);
        if (folder is null) return null;

        var dir = ThemeDir(folder);
        if (dir is null) return null;
        var ext = Path.GetExtension(url.Split('?')[0]);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".ogg";
        var dest = Path.Combine(dir, "theme" + ext);

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { logger.LogWarning(ex, "SetThemeFromUrl: can't create {Dir}", dir); return null; }

        if (!await animeThemes.DownloadAsync(url, dest, ct)) return null;
        item.ThemePath  = dest;
        item.ThemeTitle = "Custom";
        await db.SaveChangesAsync(ct);
        NotifyMediaItemChanged(item.FolderId);
        return item.ThemePath;
    }

    /// <summary>True for items worth a theme lookup. TMDB-identified anime come back
    /// as MediaType.Series, so we also accept Animation-genre + Japanese-language
    /// items. Donghua (Mandarin) aren't bridged via AniList — they're virtually
    /// absent from AnimeThemes — but are still attempted when they carry a MalId.</summary>
    private static bool LooksLikeAnime(MediaItem item, List<string> genres)
        => item.MediaType == MediaItemType.Anime
           || item.MalId is not null
           || !string.IsNullOrEmpty(item.ThemeTitle)   // already has a resolved theme — keep serving it
           || (genres.Any(g => g.Equals("Animation", StringComparison.OrdinalIgnoreCase))
               && string.Equals(item.Language, "Japanese", StringComparison.OrdinalIgnoreCase));

    /// <summary>Media-adjacent cache dir for heavy assets (theme music; later trailers).
    /// Lives in the media folder's <c>.animarr/&lt;folderId&gt;/</c> — NOT MediaCachePaths
    /// (the central image cache). Keeps large audio/video off the Docker data volume.
    /// The &lt;folderId&gt; subdir prevents collisions when several flat-section files
    /// share one parent .animarr dir. Returns null when no base dir can be derived.</summary>
    private static string? ThemeDir(FolderWatcher folder)
    {
        var baseDir = folder.SingleFilePath is { Length: > 0 } file
            ? Path.GetDirectoryName(file)
            : folder.Path;
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        return Path.Combine(baseDir, ".animarr", folder.Id.ToString("N"));
    }

    private static List<string> DeserialiseGenreNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, _json) ?? []; }
        catch { return []; }
    }

}
