using System.Text.Json;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Animarr.Web.Services;

/// <summary>
/// Tier 1 of the episode-resolution model: ask the configured LLM to place the
/// video files that the deterministic parser (Tier 0, <see cref="MediaFileResolver"/>)
/// left without an episode number. Verdicts are written to
/// <see cref="EpisodeFileMapping"/> as Source="llm" overrides — cached, so the
/// LLM is never re-asked on every page view, and never overriding a user's
/// manual ("manual") correction.
///
/// Files are grouped by their deterministic season (often known from the folder
/// path even when the episode isn't), and each season's TMDB episode list — with
/// titles — is handed to the LLM as the CLOSED set it may map onto. The list is
/// also the guardrail: <see cref="ILlmService.MapFilesToEpisodesAsync"/> rejects
/// any episode number not in it, so the model can't invent episodes.
///
/// No LLM configured/reachable → returns 0 and changes nothing; the deterministic
/// and manual layers stand on their own.
/// </summary>
public sealed class EpisodeLlmResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaFileResolver resolver,
    ILlmService llm,
    TmdbClient tmdb,
    ILogger<EpisodeLlmResolver> logger)
{
    /// <summary>Map the deterministically-unplaced files of one item. Returns
    /// the count of files newly assigned an episode.</summary>
    public async Task<int> ResolveAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        if (!await llm.IsAvailableAsync(ct)) return 0;

        var files = await resolver.ResolveAsync(mediaItemId, ct);
        // Only files the deterministic pass left unplaced AND that carry no
        // existing override (Source == null). Manual / prior-LLM rows are left be.
        var unresolved = files.Where(f => f.Episode is null && f.Source is null).ToList();
        if (unresolved.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return 0;

        int declaredSeasons = CountSeasons(item.SeasonsJson);
        int resolved = 0;

        foreach (var grp in unresolved.GroupBy(f => f.Season))
        {
            int? season = grp.Key;
            int seasonNum = season ?? 1;

            // Season-less files on a multi-season show used to be skipped outright
            // ("can't safely guess") — which is precisely the case a release tag
            // like "[TV-4] - 01" lands in when no pattern caught it, so the model
            // never even saw the one filename that says which season it is.
            // Instead of guessing, hand it the whole run numbered ABSOLUTELY
            // (S1E01..S4E01 → 1..79, each labelled "S04E01 · Title") and split the
            // answer back into (season, episode). Still a closed set, so the
            // guardrail inside MapFilesToEpisodesAsync holds unchanged.
            bool absoluteMode = season is null && declaredSeasons > 1;

            List<(int Number, string Name)> episodes;
            var absMap = new Dictionary<int, (int Season, int Episode)>();
            if (absoluteMode)
            {
                episodes = [];
                int cum = 0;
                // Offsets come from SeasonsJson episode counts — the same basis
                // MediaFileResolver uses, so an absolute number means the same
                // thing on both sides.
                foreach (var s in ParseSeasons(item.SeasonsJson).Where(s => s.Number > 0).OrderBy(s => s.Number))
                {
                    foreach (var (num, title) in await BuildEpisodeListAsync(item, s.Number, ct))
                    {
                        int abs = cum + num;
                        absMap[abs] = (s.Number, num);
                        episodes.Add((abs, string.IsNullOrEmpty(title)
                            ? $"S{s.Number:D2}E{num:D2}"
                            : $"S{s.Number:D2}E{num:D2} · {title}"));
                    }
                    cum += s.EpisodeCount;
                }
            }
            else
            {
                episodes = await BuildEpisodeListAsync(item, seasonNum, ct);
            }
            if (episodes.Count == 0) continue;

            var groupFiles = grp.ToList();
            var names = groupFiles.Select(f => f.FileName).ToList();
            var pairs = await llm.MapFilesToEpisodesAsync(names, episodes, ct);
            if (pairs is null || pairs.Count == 0) continue;

            foreach (var (idx, ep) in pairs)
            {
                if (idx < 0 || idx >= groupFiles.Count) continue;
                var file = groupFiles[idx];

                // In absolute mode the model answers with an absolute number;
                // translate it back BEFORE touching the DB. A number outside the
                // map can't be placed — skipping after Add() would leave an empty
                // mapping row behind for the next SaveChanges to persist.
                int rowSeason = seasonNum, rowEpisode = ep;
                if (absoluteMode)
                {
                    if (!absMap.TryGetValue(ep, out var se)) continue;
                    (rowSeason, rowEpisode) = se;
                }

                var row = await db.EpisodeFileMappings
                    .FirstOrDefaultAsync(m => m.MediaItemId == mediaItemId && m.FilePath == file.FilePath, ct);
                if (row is { Source: MappingSource.Manual }) continue; // never trump a manual fix
                if (row is null)
                {
                    row = new EpisodeFileMapping { MediaItemId = mediaItemId, FilePath = file.FilePath };
                    db.EpisodeFileMappings.Add(row);
                }

                row.Season     = rowSeason;
                row.Episode    = rowEpisode;
                row.Source     = MappingSource.Llm;
                row.Confidence = 0.8;
                row.UpdatedAt  = DateTime.UtcNow;
                resolved++;
            }
        }

        if (resolved > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation("LLM episode mapping for {Id}: placed {N}/{Total} unmatched file(s).",
            mediaItemId, resolved, unresolved.Count);
        return resolved;
    }

    /// <summary>Closed set of episodes the LLM may map onto for a season: TMDB
    /// episode numbers + titles when available, otherwise a bare 1..N range from
    /// the stored episode count (no titles).</summary>
    private async Task<List<(int Number, string Name)>> BuildEpisodeListAsync(
        MediaItem item, int seasonNum, CancellationToken ct)
    {
        if (item.TmdbId is int tmdbId && tmdbId > 0)
        {
            try
            {
                var detail = await tmdb.GetSeasonDetailAsync(tmdbId, seasonNum, ct: ct);
                if (detail is { Episodes.Count: > 0 })
                    return detail.Episodes
                        .Where(e => e.EpisodeNumber > 0)
                        .Select(e => (e.EpisodeNumber, e.Name ?? string.Empty))
                        .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "TMDB season {Season} fetch failed for {Tmdb}; using stored count.", seasonNum, tmdbId);
            }
        }

        var count = SeasonEpisodeCount(item.SeasonsJson, seasonNum);
        return count > 0
            ? Enumerable.Range(1, count).Select(n => (n, string.Empty)).ToList()
            : [];
    }

    // ─── SeasonsJson helpers ─────────────────────────────────────────────────
    // Case-insensitive: the stored casing of these fields has drifted across
    // versions, so we don't depend on "number" vs "Number".

    private sealed record SeasonLite(int Number, int EpisodeCount);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static List<SeasonLite> ParseSeasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<SeasonLite>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    private static int CountSeasons(string? json) => ParseSeasons(json).Count;

    private static int SeasonEpisodeCount(string? json, int seasonNum)
        => ParseSeasons(json).FirstOrDefault(s => s.Number == seasonNum)?.EpisodeCount ?? 0;
}
