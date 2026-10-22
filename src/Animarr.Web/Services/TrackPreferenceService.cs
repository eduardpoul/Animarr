using System.Globalization;
using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Per-profile track memory (playback plan §6.5, task 2.2): records which
/// audio / subtitles a profile picked on a title — by meaning, not by index —
/// and resolves the tracks a playback of a file should start with.
///
/// Resolution, per facet (audio, subtitles, offsets, aspect):
/// <list type="number">
/// <item>the title's memory — the season row before the title row. An
///   embedded audio choice is found again by language (+ title, codec,
///   channels as tie-breakers: "Дубляж" ≈ "Дубляж 5.1"), an external dub by
///   its group key (the same "Russian Dub" folder / ".ukr" suffix on the next
///   episode), subtitles by language + type (full / forced / signs);</item>
/// <item>the profile settings — <see cref="UserPreferences.AudioPreferredLanguage"/>
///   and <see cref="UserPreferences.SubtitlePreferredLanguage"/> ("Off",
///   "Forced" or a language);</item>
/// <item>the container's default flag;</item>
/// <item>the first track.</item>
/// </list>
/// A remembered track that the file doesn't have falls through to the next
/// step, so a missing dub never blocks playback.
/// </summary>
public sealed class TrackPreferenceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MediaFileResolver _resolver;
    private readonly ExternalTrackService _external;
    private readonly MediaProbeCache _probeCache;
    private readonly ILogger<TrackPreferenceService> _logger;

    public TrackPreferenceService(
        IDbContextFactory<AppDbContext> dbFactory,
        MediaFileResolver resolver,
        ExternalTrackService external,
        MediaProbeCache probeCache,
        ILogger<TrackPreferenceService> logger)
    {
        _dbFactory  = dbFactory;
        _resolver   = resolver;
        _external   = external;
        _probeCache = probeCache;
        _logger     = logger;
    }

    public const string KindEmbedded = "embedded";
    public const string KindExternal = "external";
    public const string KindOff      = "off";
    public const string KindAuto     = "auto";

    private const int MaxAudioOffsetMs    = 3_000;
    private const int MaxSubtitleOffsetMs = 600_000;

    /// <summary>What <see cref="TrackPrefsUpdateRequest.Clear"/> may name:
    /// "audioOffset" = the offset of the request's / current audio,
    /// "audioOffsets" = every remembered audio offset.</summary>
    private static readonly string[] ClearableFacets =
        ["audio", "subtitle", "audioOffset", "audioOffsets", "subtitleOffset", "aspect"];

    // ─── File → title ─────────────────────────────────────────────────────────

    /// <summary>The title a library file belongs to and its season.</summary>
    public sealed record Location(Guid MediaItemId, int? Season, bool IsMovie);

    /// <summary>The owning folder's title (the same owner pick as the
    /// external-track sweep) and the file's season from
    /// <see cref="MediaFileResolver"/> (overrides included). Null outside any
    /// identified title.</summary>
    public async Task<Location?> LocateAsync(string fullPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(fullPath);
        Guid itemId;
        bool isMovie;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var folders = await db.FolderWatchers.AsNoTracking()
                .Where(f => f.Path != null && f.Path != "")
                .OrderBy(f => f.Id)
                .Select(f => new ExternalTrackService.OwnerCandidate(f.Id, f.Path, f.IsSection, f.SingleFilePath))
                .ToListAsync(ct);
            var owner = ExternalTrackService.PickOwner(folders, full);
            if (owner is null) return null;
            var item = await db.MediaItems.AsNoTracking()
                .Where(m => m.FolderId == owner.Id)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.MediaType })
                .FirstOrDefaultAsync(ct);
            if (item is null) return null;
            itemId  = item.Id;
            isMovie = item.MediaType == MediaItemType.Movie;
        }
        if (isMovie) return new Location(itemId, null, true);

        int? season = null;
        try
        {
            var files = await _resolver.ResolveAsync(itemId, ct);
            season = files.FirstOrDefault(f => SamePath(f.FilePath, full))?.Season;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Track prefs: could not resolve the season of {Path}", full);
        }
        return new Location(itemId, season, false);
    }

    // ─── Embedded tracks ──────────────────────────────────────────────────────

    /// <summary>One embedded audio / subtitle stream; <see cref="Index"/> is
    /// type-relative (0:a:N / 0:s:N).</summary>
    internal sealed record EmbeddedTrack(int Index, string? Language, string? Title, string? Codec,
        int? Channels, bool IsDefault, bool IsForced);

    internal sealed record EmbeddedTracks(List<EmbeddedTrack> Audio, List<EmbeddedTrack> Subtitles);

    private async Task<EmbeddedTracks> ProbeEmbeddedAsync(string fullPath, CancellationToken ct)
    {
        var audio = new List<EmbeddedTrack>();
        var subs  = new List<EmbeddedTrack>();
        var json  = await _probeCache.GetStreamsJsonAsync(fullPath, ct);
        if (json is null) return new(audio, subs);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return new(audio, subs);
        foreach (var s in streams.EnumerateArray())
        {
            var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            var list = type switch { "audio" => audio, "subtitle" => subs, _ => null };
            if (list is null) continue;
            string? lang = null, title = null;
            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Value.ValueKind != JsonValueKind.String) continue;
                    if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = tag.Value.GetString();
                    else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = tag.Value.GetString();
                }
            bool isDefault = false, isForced = false;
            if (s.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object)
            {
                isDefault = disp.TryGetProperty("default", out var d) && d.TryGetInt32(out var dv) && dv == 1;
                isForced  = disp.TryGetProperty("forced",  out var f) && f.TryGetInt32(out var fv) && fv == 1;
            }
            list.Add(new EmbeddedTrack(list.Count, NormLang(lang),
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null,
                s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var c) ? c : null,
                isDefault, isForced));
        }
        return new(audio, subs);
    }

    // ─── Resolve ──────────────────────────────────────────────────────────────

    /// <summary><see cref="ResolveAsync"/> that never throws: null when the
    /// defaults can't be worked out (the playback start goes on without).</summary>
    public async Task<TrackDefaultsDto?> TryResolveAsync(Guid? userId, string fullPath, CancellationToken ct)
    {
        try { return await ResolveAsync(userId, fullPath, ct); }
        catch (Exception ex)
        {
            // A cancelled request needs no log line — the caller notices on its
            // next await.
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                _logger.LogWarning(ex, "Track defaults could not be resolved for {Path}", fullPath);
            return null;
        }
    }

    /// <summary>The tracks a playback of <paramref name="fullPath"/> (already
    /// validated) should start with for <paramref name="userId"/> (null: no
    /// memory, no profile — flags and first track only).</summary>
    public async Task<TrackDefaultsDto> ResolveAsync(Guid? userId, string fullPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(fullPath);
        var locTask      = LocateAsync(full, ct);
        var embeddedTask = ProbeEmbeddedAsync(full, ct);
        var externalTask = _external.FindForVideoAsync(full, probe: false, ct);
        await Task.WhenAll(locTask, embeddedTask, externalTask);
        var loc      = locTask.Result;
        var embedded = embeddedTask.Result;
        var external = externalTask.Result;

        TrackPreference? seasonRow = null, titleRow = null;
        UserPreferences? profile = null;
        if (userId is Guid uid)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (loc is not null)
            {
                var rows = await db.TrackPreferences.AsNoTracking()
                    .Where(p => p.UserId == uid && p.MediaItemId == loc.MediaItemId
                                && (p.Season == null || p.Season == loc.Season))
                    .ToListAsync(ct);
                titleRow  = rows.FirstOrDefault(r => r.Season is null);
                seasonRow = loc.Season is int s ? rows.FirstOrDefault(r => r.Season == s) : null;
            }
            profile = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == uid, ct)
                      ?? new UserPreferences { UserId = uid };
        }

        var extAudio = external.Where(t => t.Kind == "audio").ToList();
        // Text sidecars before bitmap ones (.sup / .idx), like the embedded picks.
        var extSubs  = external.Where(t => t.Kind == "subtitle").OrderBy(t => t.Image == true ? 1 : 0).ToList();
        var audio    = ResolveAudio(embedded.Audio, extAudio, seasonRow, titleRow, profile);
        var subtitle = ResolveSubtitle(embedded.Subtitles, extSubs, seasonRow, titleRow, profile, audio?.Language);

        // The audio's sync offset: remembered for it (0 included), else the
        // DELAY a dub's file name states, else none.
        var offsets = ReadOffsets(titleRow?.AudioOffsetsJson);
        foreach (var (k, v) in ReadOffsets(seasonRow?.AudioOffsetsJson)) offsets[k] = v;
        int audioOffset = 0;
        string offsetSource = "none";
        if (audio is not null && offsets.TryGetValue(AudioKey(audio.Kind, audio.Language, audio.GroupKey), out var ms))
            (audioOffset, offsetSource) = (Math.Clamp(ms, -MaxAudioOffsetMs, MaxAudioOffsetMs), "memory");
        else if (audio is { Kind: KindExternal, DelayMs: int delay } && delay != 0)
            (audioOffset, offsetSource) = (Math.Clamp(delay, -MaxAudioOffsetMs, MaxAudioOffsetMs), "delay");

        return new TrackDefaultsDto(
            MediaItemId:       loc?.MediaItemId,
            Season:            loc?.Season,
            Audio:             audio,
            Subtitle:          subtitle,
            SubtitleOffsetMs:  seasonRow?.SubtitleOffsetMs ?? titleRow?.SubtitleOffsetMs ?? 0,
            AudioOffsetMs:     audioOffset,
            Aspect:            seasonRow?.Aspect ?? titleRow?.Aspect,
            AudioOffsetSource: offsetSource);
    }

    /// <summary><paramref name="defaults"/> with <see cref="TrackDefaultAudioDto.Applied"/>
    /// set — whether the stream a start returned already carries that audio.</summary>
    public static TrackDefaultsDto? MarkApplied(TrackDefaultsDto? defaults, bool applied)
        => defaults?.Audio is { } a ? defaults with { Audio = a with { Applied = applied } } : defaults;

    /// <summary><see cref="TrackDefaultsDto.AudioOffsetSkipReason"/>: Direct Play
    /// kept for a <c>localTracks</c> client — the device applies the offset
    /// itself if it can.</summary>
    public const string OffsetSkippedLocalTracks = "local_tracks";
    /// <summary><see cref="TrackDefaultsDto.AudioOffsetSkipReason"/>: the plan
    /// the stream got takes no offset (TS copy of the file's own audio).</summary>
    public const string OffsetSkippedPlan = "plan";

    /// <summary><paramref name="defaults"/> with <see cref="TrackDefaultsDto.AudioOffsetApplied"/>
    /// set — whether the start added its audio offset to the stream's sync —
    /// and, when it didn't although it was asked to, why
    /// (<paramref name="skipReason"/>).</summary>
    public static TrackDefaultsDto? MarkOffsetApplied(TrackDefaultsDto? defaults, bool applied, string? skipReason = null)
        => defaults is null ? null
         : defaults with { AudioOffsetApplied = applied, AudioOffsetSkipReason = applied ? null : skipReason };

    private static TrackDefaultAudioDto? ResolveAudio(List<EmbeddedTrack> emb, List<ExternalTrackDto> ext,
        TrackPreference? seasonRow, TrackPreference? titleRow, UserPreferences? profile)
    {
        if (emb.Count == 0 && ext.Count == 0) return null;

        foreach (var (row, source) in new[] { (seasonRow, "memory-season"), (titleRow, "memory") })
        {
            if (row?.AudioKind is not string kind) continue;
            if (kind == KindExternal && MatchExternal(ext, row.AudioGroupKey) is { } x) return Audio(x, source);
            if (kind == KindEmbedded && MatchEmbeddedAudio(emb, row) is { } e) return Audio(e, source);
        }

        if (PreferredLanguage(profile?.AudioPreferredLanguage) is string lang)
        {
            var hit = emb.Where(t => t.Language == lang)
                .OrderBy(t => IsCommentary(t.Title) ? 1 : 0)
                .ThenBy(t => t.IsDefault ? 0 : 1)
                .ThenBy(t => t.Index)
                .FirstOrDefault();
            if (hit is not null) return Audio(hit, "profile");
            var dub = ext.FirstOrDefault(t => t.Language == lang);
            if (dub is not null) return Audio(dub, "profile");
        }

        if (emb.FirstOrDefault(t => t.IsDefault) is { } def) return Audio(def, "default");
        return emb.Count > 0 ? Audio(emb[0], "first") : Audio(ext[0], "first");
    }

    /// <summary>
    /// Subtitle defaults. <see cref="UserPreferences.SubtitlePreferredLanguage"/>
    /// is a language ("Always" in the settings: that language, else the file's
    /// flags, else the first track) or one of three modes — the same four the
    /// web player and the TV offer:
    /// <list type="bullet">
    /// <item>"Off" — none;</item>
    /// <item>"Forced" — a forced / signs track (one in the audio's language
    ///   first), else none;</item>
    /// <item>"Default" — what the file flags: its default track, else a
    ///   forced / signs track in the audio's language (a foreign-language
    ///   scene), else none.</item>
    /// </list>
    /// <paramref name="audioLanguage"/> is the language of the audio the start
    /// resolved to.
    /// </summary>
    private static TrackDefaultSubtitleDto ResolveSubtitle(List<EmbeddedTrack> emb, List<ExternalTrackDto> ext,
        TrackPreference? seasonRow, TrackPreference? titleRow, UserPreferences? profile, string? audioLanguage)
    {
        foreach (var (row, source) in new[] { (seasonRow, "memory-season"), (titleRow, "memory") })
        {
            if (row?.SubtitleKind is not string kind) continue;
            if (kind == KindOff) return Off(source);
            if (MatchSubtitle(emb, ext, row) is { } hit) return hit with { Source = source };
        }

        // A forced / signs track in the audio's language (embedded first).
        TrackDefaultSubtitleDto? ForcedInAudioLanguage()
        {
            if (audioLanguage is null) return null;
            var e = TextFirst(emb).FirstOrDefault(t => t.Language == audioLanguage && SubType(t) != "full");
            if (e is not null) return Subtitle(e, "profile");
            var x = ext.FirstOrDefault(t => t.Language == audioLanguage && t.SubtitleType is "forced" or "signs");
            return x is not null ? Subtitle(x, "profile") : null;
        }

        if (profile is not null)
        {
            var pref = profile.SubtitlePreferredLanguage?.Trim();
            if (string.Equals(pref, "Off", StringComparison.OrdinalIgnoreCase)) return Off("profile");
            if (string.Equals(pref, "Forced", StringComparison.OrdinalIgnoreCase))
            {
                if (ForcedInAudioLanguage() is { } inAudio) return inAudio;
                var forced = TextFirst(emb).FirstOrDefault(t => SubType(t) != "full");
                if (forced is not null) return Subtitle(forced, "profile");
                var forcedExt = ext.FirstOrDefault(t => t.SubtitleType is "forced" or "signs");
                return forcedExt is not null ? Subtitle(forcedExt, "profile") : Off("profile");
            }
            if (string.Equals(pref, "Default", StringComparison.OrdinalIgnoreCase))
            {
                if (TextFirst(emb).FirstOrDefault(t => t.IsDefault) is { } flagged) return Subtitle(flagged, "default");
                return ForcedInAudioLanguage() ?? Off("profile");
            }
            if (PreferredLanguage(pref) is string lang)
            {
                var full = TextFirst(emb).FirstOrDefault(t => t.Language == lang && SubType(t) == "full");
                if (full is not null) return Subtitle(full, "profile");
                var fullExt = ext.FirstOrDefault(t => t.Language == lang && t.SubtitleType is null or "full");
                if (fullExt is not null) return Subtitle(fullExt, "profile");
                var any = TextFirst(emb).FirstOrDefault(t => t.Language == lang);
                if (any is not null) return Subtitle(any, "profile");
                var anyExt = ext.FirstOrDefault(t => t.Language == lang);
                if (anyExt is not null) return Subtitle(anyExt, "profile");
            }
        }

        if (TextFirst(emb).FirstOrDefault(t => t.IsDefault) is { } def) return Subtitle(def, "default");
        if (TextFirst(emb).FirstOrDefault() is { } first) return Subtitle(first, "first");
        if (ext.Count > 0) return Subtitle(ext[0], "first");
        return Off("none");
    }

    // ─── Matching a remembered choice on another file ─────────────────────────

    /// <summary>The external track with <paramref name="groupKey"/>; else the
    /// only one under its base key (the "|name:…" disambiguator differs when
    /// an episode has one dub fewer).</summary>
    private static ExternalTrackDto? MatchExternal(List<ExternalTrackDto> ext, string? groupKey)
    {
        if (string.IsNullOrEmpty(groupKey)) return null;
        var exact = ext.FirstOrDefault(t => t.GroupKey == groupKey);
        if (exact is not null) return exact;
        var baseKey = ExternalTrackNaming.BaseGroupKey(groupKey);
        var same = ext.Where(t => t.GroupKey is not null && ExternalTrackNaming.BaseGroupKey(t.GroupKey) == baseKey).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>Same language (required when one was remembered), then the
    /// closest title, codec and channel count; the lowest index on a tie.
    /// Without a remembered language the title has to match.</summary>
    private static EmbeddedTrack? MatchEmbeddedAudio(List<EmbeddedTrack> emb, TrackPreference row)
    {
        EmbeddedTrack? best = null;
        int bestScore = -1;
        foreach (var t in emb)
        {
            if (row.AudioLanguage is not null && t.Language != row.AudioLanguage) continue;
            int title = TitleScore(row.AudioTitle, t.Title);
            if (row.AudioLanguage is null && (row.AudioTitle is null || title < 2)) continue;
            int score = title
                + (row.AudioCodec is not null && string.Equals(row.AudioCodec, t.Codec, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                + (row.AudioChannels is not null && row.AudioChannels == t.Channels ? 1 : 0);
            if (score > bestScore) { best = t; bestScore = score; }
        }
        return best;
    }

    /// <summary>A remembered subtitle on another file: the same kind first
    /// (external by group key, embedded by language + type + title), then the
    /// other kind with the same language and type — "Russian subtitles" is the
    /// meaning, whether they are muxed or a sidecar on this episode.</summary>
    private static TrackDefaultSubtitleDto? MatchSubtitle(List<EmbeddedTrack> emb, List<ExternalTrackDto> ext,
        TrackPreference row)
    {
        var type = row.SubtitleType ?? "full";
        EmbeddedTrack? Embedded()
        {
            EmbeddedTrack? best = null;
            int bestScore = -1;
            foreach (var t in TextFirst(emb))
            {
                if (SubType(t) != type) continue;
                if (row.SubtitleLanguage is not null && t.Language != row.SubtitleLanguage) continue;
                int title = TitleScore(row.SubtitleTitle, t.Title);
                if (row.SubtitleLanguage is null && (row.SubtitleTitle is null || title < 2)) continue;
                if (title > bestScore) { best = t; bestScore = title; }
            }
            return best;
        }
        ExternalTrackDto? ExternalByMeaning()
            => row.SubtitleLanguage is null ? null
             : ext.FirstOrDefault(t => t.Language == row.SubtitleLanguage && (t.SubtitleType ?? "full") == type);

        if (row.SubtitleKind == KindExternal)
        {
            if (MatchExternal(ext, row.SubtitleGroupKey) is { } x) return Subtitle(x, "");
            if (ExternalByMeaning() is { } y) return Subtitle(y, "");
            return Embedded() is { } e ? Subtitle(e, "") : null;
        }
        if (Embedded() is { } hit) return Subtitle(hit, "");
        return ExternalByMeaning() is { } z ? Subtitle(z, "") : null;
    }

    /// <summary>4 = same title (both none counts), 2 = one contains the other
    /// ("Дубляж" / "Дубляж 5.1"), 0 = different.</summary>
    private static int TitleScore(string? remembered, string? candidate)
    {
        var a = NormTitle(remembered);
        var b = NormTitle(candidate);
        if (a == b) return 4;
        if (a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
            return 2;
        return 0;
    }

    private static string NormTitle(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : ExternalTrackNaming.Normalize(s);

    private static bool IsCommentary(string? title)
        => title is not null && (title.Contains("comment", StringComparison.OrdinalIgnoreCase)
                                 || title.Contains("коммент", StringComparison.OrdinalIgnoreCase));

    /// <summary>"full" / "forced" / "signs" of an embedded subtitle: the
    /// title's words (Signs &amp; Songs, Надписи, Forced), then the forced
    /// disposition — the same reading the web player's picker uses.</summary>
    private static string SubType(EmbeddedTrack t)
    {
        var type = ExternalTrackNaming.SubtitleTypeOf(t.Title);
        return type == "full" && t.IsForced ? "forced" : type;
    }

    /// <summary>Text tracks before bitmap ones (a browser can't render PGS /
    /// VobSub yet), each group in stream order.</summary>
    private static IEnumerable<EmbeddedTrack> TextFirst(List<EmbeddedTrack> subs)
        => subs.OrderBy(t => SubtitleCodecs.IsImage(t.Codec) ? 1 : 0).ThenBy(t => t.Index);

    // ─── Languages ────────────────────────────────────────────────────────────

    /// <summary>A stream language tag → the canonical ISO 639-2 code the
    /// external-track names use ("ru" / "rus" / "russian" → "rus"); unknown
    /// tags are kept lower-cased; "und" / empty → null.</summary>
    internal static string? NormLang(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        if (t.Equals("und", StringComparison.OrdinalIgnoreCase) || t.Equals("unk", StringComparison.OrdinalIgnoreCase))
            return null;
        return ExternalTrackNaming.FromLanguageTag(t)?.Code ?? t.ToLowerInvariant();
    }

    /// <summary>A profile language setting ("Japanese", "Mandarin") → the same
    /// canonical code; null for modes ("Off", "Forced") and unknown names.</summary>
    private static string? PreferredLanguage(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        return ExternalTrackNaming.FromLanguageTag(setting)?.Code
               ?? ExternalTrackNaming.FromLanguageTag(Animarr.Shared.LanguageNameMap.ToIso639(setting))?.Code;
    }

    // ─── DTO assembly ─────────────────────────────────────────────────────────

    private static TrackDefaultAudioDto Audio(EmbeddedTrack t, string source)
        => new(KindEmbedded, t.Index, null, t.Language, t.Title, t.Codec, t.Channels, null, null, null, source);

    private static TrackDefaultAudioDto Audio(ExternalTrackDto t, string source)
        => new(KindExternal, null, t.Path, t.Language, null, t.Codec, t.Channels, t.GroupKey, t.Label, t.DelayMs, source);

    private static TrackDefaultSubtitleDto Subtitle(EmbeddedTrack t, string source)
        => new(KindEmbedded, t.Index, null, t.Language, SubType(t), t.Title, t.Codec, null, null, source);

    private static TrackDefaultSubtitleDto Subtitle(ExternalTrackDto t, string source)
        => new(KindExternal, null, t.Path, t.Language, t.SubtitleType ?? "full", null, t.Codec ?? t.Ext, t.GroupKey, t.Label, source);

    private static TrackDefaultSubtitleDto Off(string source)
        => new(KindOff, null, null, null, null, null, null, null, null, source);

    /// <summary>Key of an audio choice in <see cref="TrackPreference.AudioOffsetsJson"/>:
    /// the dub's base group key, or the embedded track's language (muxed tracks
    /// of one language share their sync).</summary>
    private static string AudioKey(string kind, string? language, string? groupKey)
        => kind == KindExternal && !string.IsNullOrEmpty(groupKey)
            ? "ext:" + ExternalTrackNaming.BaseGroupKey(groupKey)
            : "emb:" + (language ?? "und");

    private static Dictionary<string, int> ReadOffsets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) is { } d
                ? new Dictionary<string, int>(d, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ─── Stored rows ──────────────────────────────────────────────────────────

    public async Task<TrackPrefsDto> GetAsync(Guid userId, Guid mediaItemId, int? season, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.TrackPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.MediaItemId == mediaItemId && p.Season == season, ct);
        return ToDto(mediaItemId, season, row);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid mediaItemId, int? season, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TrackPreferences
            .Where(p => p.UserId == userId && p.MediaItemId == mediaItemId && p.Season == season)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Outcome of <see cref="UpdateAsync"/>: the stored row, or why the
    /// request was refused (<see cref="Status"/> 400 / 404).</summary>
    public sealed record UpdateResult(TrackPrefsDto? Prefs, int Status, string? Error)
    {
        public static UpdateResult Fail(int status, string error) => new(null, status, error);
    }

    /// <summary>
    /// Partial update of the (user, title, season) row from what the client
    /// picked: an embedded index / external path on <paramref name="videoFullPath"/>
    /// becomes language + title (+ codec / channels) or the dub's group key.
    /// <paramref name="videoFullPath"/> must already be validated by the
    /// caller (library roots + the profile's folders).
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(Guid userId, Guid mediaItemId, int? season,
        TrackPrefsUpdateRequest req, string? videoFullPath, CancellationToken ct)
    {
        var audioKind = req.Audio?.Kind?.Trim().ToLowerInvariant();
        var subKind   = req.Subtitle?.Kind?.Trim().ToLowerInvariant();
        if (audioKind is not (null or KindEmbedded or KindExternal or KindAuto))
            return UpdateResult.Fail(400, "audio.kind must be embedded, external or auto.");
        if (subKind is not (null or KindEmbedded or KindExternal or KindOff or KindAuto))
            return UpdateResult.Fail(400, "subtitle.kind must be embedded, external, off or auto.");
        if (req.Aspect is { Length: > 32 } || (req.Aspect is { } asp && asp.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '-' or '_'))))
            return UpdateResult.Fail(400, "aspect must be at most 32 characters of letters, digits, ':', '.', '-' or '_'.");

        bool choiceNeedsFile = audioKind is KindEmbedded or KindExternal || subKind is KindEmbedded or KindExternal;
        if (choiceNeedsFile && videoFullPath is null)
            return UpdateResult.Fail(400, "path is required to record a track choice.");
        await using (var check = await _dbFactory.CreateDbContextAsync(ct))
            if (!await check.MediaItems.AnyAsync(m => m.Id == mediaItemId, ct))
                return UpdateResult.Fail(404, "Unknown title.");

        EmbeddedTracks? embedded = null;
        IReadOnlyList<ExternalTrackDto>? external = null;
        if (videoFullPath is not null)
        {
            var loc = await LocateAsync(videoFullPath, ct);
            if (loc?.MediaItemId != mediaItemId)
                return UpdateResult.Fail(400, "path does not belong to this title.");
        }

        async Task<EmbeddedTracks> Embedded() => embedded ??= await ProbeEmbeddedAsync(videoFullPath!, ct);
        async Task<IReadOnlyList<ExternalTrackDto>> External()
            => external ??= await _external.FindForVideoAsync(videoFullPath!, probe: true, ct);

        // ── Derive the meaning of each pick before touching the row ──────────
        TrackAudioPrefDto? audioPick = null;
        if (audioKind == KindEmbedded)
        {
            var list = (await Embedded()).Audio;
            if (req.Audio!.Index is not int ai || ai < 0 || ai >= list.Count)
                return UpdateResult.Fail(400, $"audio.index must be 0..{list.Count - 1} for this file.");
            var t = list[ai];
            audioPick = new TrackAudioPrefDto(KindEmbedded, t.Language, t.Title, t.Codec, t.Channels, null);
        }
        else if (audioKind == KindExternal)
        {
            var x = FindExternal(await External(), "audio", req.Audio!.Path);
            if (x is null) return UpdateResult.Fail(400, "audio.path is not an external audio track of this file.");
            audioPick = new TrackAudioPrefDto(KindExternal, x.Language, null, x.Codec, x.Channels, x.GroupKey);
        }

        TrackSubtitlePrefDto? subPick = null;
        if (subKind == KindEmbedded)
        {
            var list = (await Embedded()).Subtitles;
            if (req.Subtitle!.Index is not int si || si < 0 || si >= list.Count)
                return UpdateResult.Fail(400, $"subtitle.index must be 0..{list.Count - 1} for this file.");
            var t = list[si];
            subPick = new TrackSubtitlePrefDto(KindEmbedded, t.Language, SubType(t), t.Title, null);
        }
        else if (subKind == KindExternal)
        {
            var x = FindExternal(await External(), "subtitle", req.Subtitle!.Path);
            if (x is null) return UpdateResult.Fail(400, "subtitle.path is not an external subtitle of this file.");
            subPick = new TrackSubtitlePrefDto(KindExternal, x.Language, x.SubtitleType ?? "full", null, x.GroupKey);
        }

        // "clear": facets to forget (back to the defaults) — applied after the
        // values of this request, so clearing wins over setting the same facet.
        var clear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in req.Clear ?? [])
        {
            var name = c?.Trim() ?? "";
            if (!ClearableFacets.Contains(name, StringComparer.OrdinalIgnoreCase))
                return UpdateResult.Fail(400, $"clear accepts {string.Join(", ", ClearableFacets)} (got \"{name}\").");
            clear.Add(name);
        }

        // The audio an offset belongs to: this request's pick, else what the
        // file plays now for this profile, else the row's remembered audio.
        string? offsetKey = null;
        if (req.AudioOffsetMs is not null || clear.Contains("audioOffset"))
        {
            if (audioPick is not null) offsetKey = AudioKey(audioPick.Kind, audioPick.Language, audioPick.GroupKey);
            else if (videoFullPath is not null && (await ResolveAsync(userId, videoFullPath, ct)).Audio is { } cur)
                offsetKey = AudioKey(cur.Kind, cur.Language, cur.GroupKey);
        }

        for (int attempt = 0; ; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.TrackPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.MediaItemId == mediaItemId && p.Season == season, ct);
            bool isNew = row is null;
            row ??= new TrackPreference { Id = Guid.NewGuid(), UserId = userId, MediaItemId = mediaItemId, Season = season };

            if (audioKind == KindAuto)
                (row.AudioKind, row.AudioLanguage, row.AudioTitle, row.AudioCodec, row.AudioChannels, row.AudioGroupKey)
                    = (null, null, null, null, null, null);
            else if (audioPick is not null)
                (row.AudioKind, row.AudioLanguage, row.AudioTitle, row.AudioCodec, row.AudioChannels, row.AudioGroupKey)
                    = (audioPick.Kind, audioPick.Language, audioPick.Title, audioPick.Codec, audioPick.Channels, audioPick.GroupKey);

            if (subKind == KindAuto)
                (row.SubtitleKind, row.SubtitleLanguage, row.SubtitleType, row.SubtitleTitle, row.SubtitleGroupKey)
                    = (null, null, null, null, null);
            else if (subKind == KindOff)
                (row.SubtitleKind, row.SubtitleLanguage, row.SubtitleType, row.SubtitleTitle, row.SubtitleGroupKey)
                    = (KindOff, null, null, null, null);
            else if (subPick is not null)
                (row.SubtitleKind, row.SubtitleLanguage, row.SubtitleType, row.SubtitleTitle, row.SubtitleGroupKey)
                    = (subPick.Kind, subPick.Language, subPick.Type, subPick.Title, subPick.GroupKey);

            // Values are stored as given — an explicit 0 is remembered as 0
            // (it beats a dub's DELAY tag); forgetting is "clear".
            if (req.SubtitleOffsetMs is int so)
                row.SubtitleOffsetMs = Math.Clamp(so, -MaxSubtitleOffsetMs, MaxSubtitleOffsetMs);

            if (req.AudioOffsetMs is not null || clear.Contains("audioOffset"))
            {
                var key = offsetKey ?? (row.AudioKind is string k ? AudioKey(k, row.AudioLanguage, row.AudioGroupKey) : null);
                if (key is null)
                    return UpdateResult.Fail(400, "audioOffsetMs / clear audioOffset needs an audio track: send audio or path.");
                var offsets = ReadOffsets(row.AudioOffsetsJson);
                if (req.AudioOffsetMs is int ao) offsets[key] = Math.Clamp(ao, -MaxAudioOffsetMs, MaxAudioOffsetMs);
                if (clear.Contains("audioOffset")) offsets.Remove(key);
                row.AudioOffsetsJson = offsets.Count == 0 ? null : JsonSerializer.Serialize(offsets);
            }

            if (req.Aspect is not null)
                row.Aspect = req.Aspect.Length == 0 ? null : req.Aspect;   // "" = clear (older clients)

            if (clear.Contains("audio"))
                (row.AudioKind, row.AudioLanguage, row.AudioTitle, row.AudioCodec, row.AudioChannels, row.AudioGroupKey)
                    = (null, null, null, null, null, null);
            if (clear.Contains("subtitle"))
                (row.SubtitleKind, row.SubtitleLanguage, row.SubtitleType, row.SubtitleTitle, row.SubtitleGroupKey)
                    = (null, null, null, null, null);
            if (clear.Contains("subtitleOffset")) row.SubtitleOffsetMs = null;
            if (clear.Contains("audioOffsets"))   row.AudioOffsetsJson = null;
            if (clear.Contains("aspect"))         row.Aspect = null;

            row.UpdatedAt = DateTime.UtcNow;
            bool empty = row.AudioKind is null && row.SubtitleKind is null && row.SubtitleOffsetMs is null
                         && row.AudioOffsetsJson is null && row.Aspect is null;
            if (isNew && !empty) db.TrackPreferences.Add(row);
            else if (!isNew && empty) db.TrackPreferences.Remove(row);

            try
            {
                await db.SaveChangesAsync(ct);
                return new UpdateResult(ToDto(mediaItemId, season, empty ? null : row), 200, null);
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // A concurrent PUT inserted the row first (unique index) —
                // apply this update on top of it.
            }
        }
    }

    private static ExternalTrackDto? FindExternal(IReadOnlyList<ExternalTrackDto> tracks, string kind, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }
        return tracks.FirstOrDefault(t => t.Kind == kind && SamePath(t.Path, full));
    }

    private static TrackPrefsDto ToDto(Guid mediaItemId, int? season, TrackPreference? row)
        => new(
            MediaItemId: mediaItemId,
            Season:      season,
            Audio:       row?.AudioKind is string ak
                ? new TrackAudioPrefDto(ak, row.AudioLanguage, row.AudioTitle, row.AudioCodec, row.AudioChannels, row.AudioGroupKey)
                : null,
            Subtitle:    row?.SubtitleKind is string sk
                ? new TrackSubtitlePrefDto(sk, row.SubtitleLanguage, row.SubtitleType, row.SubtitleTitle, row.SubtitleGroupKey)
                : null,
            SubtitleOffsetMs: row?.SubtitleOffsetMs,
            AudioOffsets:     ReadOffsets(row?.AudioOffsetsJson),
            Aspect:           row?.Aspect,
            UpdatedAt:        row?.UpdatedAt);
}
