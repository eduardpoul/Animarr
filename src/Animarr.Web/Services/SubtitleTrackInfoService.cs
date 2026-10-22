using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Animarr.Shared.Models;

namespace Animarr.Web.Services;

/// <summary>
/// What a player needs to pick a subtitle renderer per track
/// (<c>/api/subtitle/tracks</c>): text vs bitmap, the ASS analysis
/// (<see cref="AssAnalyzer"/> — JASSUB or plain cues) and the fonts attached
/// to the file. The analysis reads the cached original script; an embedded
/// ASS track is only copied out when the caller asks (<c>analyze</c>) because
/// that is a full read of the container — one read for all its ASS tracks.
/// </summary>
public sealed class SubtitleTrackInfoService
{
    private readonly MediaProbeCache _probeCache;
    private readonly SubtitleFontService _fonts;
    private readonly string _cacheRoot;
    private readonly ILogger<SubtitleTrackInfoService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Stamp, AssAnalyzer.Info Info)> _analyses = new();

    public SubtitleTrackInfoService(MediaProbeCache probeCache, SubtitleFontService fonts, MediaCachePaths paths,
        ILogger<SubtitleTrackInfoService> logger)
    {
        _probeCache = probeCache;
        _fonts      = fonts;
        _cacheRoot  = paths.CacheRoot;
        _logger     = logger;
    }

    /// <summary>One subtitle stream of the probe; <see cref="Index"/> = s:N.</summary>
    internal sealed record Stream(int Index, string? Codec, string? Language, string? Title, bool IsDefault, bool IsForced);

    internal static List<Stream> SubtitleStreams(string? probeJson)
    {
        var list = new List<Stream>();
        if (string.IsNullOrWhiteSpace(probeJson)) return list;
        using var doc = JsonDocument.Parse(probeJson);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "subtitle") continue;
            string? lang = null, title = null;
            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Value.ValueKind != JsonValueKind.String) continue;
                    if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = tag.Value.GetString();
                    else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = tag.Value.GetString();
                }
            bool def = false, forced = false;
            if (s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                def    = d.TryGetProperty("default", out var dv) && dv.TryGetInt32(out var di) && di == 1;
                forced = d.TryGetProperty("forced", out var fv) && fv.TryGetInt32(out var fi) && fi == 1;
            }
            list.Add(new Stream(list.Count, s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null,
                TrackPreferenceService.NormLang(lang), string.IsNullOrWhiteSpace(title) ? null : title.Trim(), def, forced));
        }
        return list;
    }

    /// <summary>The subtitle tracks + fonts of <paramref name="fullPath"/>
    /// (already validated).</summary>
    public async Task<SubtitleTracksDto> ListAsync(string fullPath, bool analyze, CancellationToken ct)
    {
        var streams = SubtitleStreams(await _probeCache.GetStreamsJsonAsync(fullPath, ct));
        bool standalone = SubtitleCharset.IsTextSubtitleFile(fullPath);
        var assTracks = streams.Where(s => SubtitleCodecs.IsAss(s.Codec)).Select(s => s.Index).ToList();

        if (assTracks.Count > 0)
        {
            try
            {
                // A standalone script is read as is (cheap) — always analyzed.
                if (standalone)
                    await SubtitleCache.GetOrExtractAsync(fullPath, 0, "ass", _cacheRoot, ct);
                else if (analyze)
                    await SubtitleCache.EnsureOriginalAssAsync(fullPath, assTracks, _cacheRoot, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ASS tracks of {Path} could not be copied out for analysis", fullPath);
            }
        }

        var escaped = Uri.EscapeDataString(fullPath);
        var tracks = new List<SubtitleTrackInfoDto>(streams.Count);
        foreach (var s in streams)
        {
            bool image = SubtitleCodecs.IsImage(s.Codec);
            AssInfoDto? ass = null;
            if (SubtitleCodecs.IsAss(s.Codec)
                && SubtitleCache.TryGetCached(fullPath, s.Index, "ass", _cacheRoot) is { } script
                && await AnalyzeAsync(script, ct) is { } info)
                ass = new AssInfoDto(info.Styles, info.Events, info.Complex, info.Reasons, info.Fonts);

            var track = s.Index.ToString(CultureInfo.InvariantCulture);
            tracks.Add(new SubtitleTrackInfoDto(
                Index:     s.Index,
                Codec:     s.Codec,
                Kind:      image ? "image" : "text",
                Language:  s.Language,
                Title:     s.Title,
                IsDefault: s.IsDefault,
                IsForced:  s.IsForced,
                Ass:       ass,
                VttUrl:    image ? null : $"/api/subtitle?path={escaped}&track={track}",
                AssUrl:    image ? null : $"/api/subtitle?path={escaped}&track={track}&format=ass",
                SupUrl:    SubtitleCodecs.IsPgs(s.Codec) ? $"/api/subtitle/sup?path={escaped}&track={track}" : null));
        }

        IReadOnlyList<SubtitleFontDto> fonts = [];
        if (assTracks.Count > 0 && !standalone)
            fonts = (await _fonts.ListAsync(fullPath, ct)).Select(f => ToDto(f, escaped)).ToList();
        return new SubtitleTracksDto(tracks, fonts);
    }

    /// <summary>ffprobe codec_name of subtitle stream s:<paramref name="track"/>;
    /// null when there is no such stream.</summary>
    public async Task<string?> CodecOfAsync(string fullPath, int track, CancellationToken ct)
        => SubtitleStreams(await _probeCache.GetStreamsJsonAsync(fullPath, ct)).FirstOrDefault(s => s.Index == track)?.Codec;

    public async Task<IReadOnlyList<SubtitleFontDto>> FontsAsync(string fullPath, CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(fullPath);
        return (await _fonts.ListAsync(fullPath, ct)).Select(f => ToDto(f, escaped)).ToList();
    }

    private static SubtitleFontDto ToDto(SubtitleFontService.Font f, string escapedPath)
        => new(f.Id, f.FileName, f.MimeType, f.SizeBytes, f.Family, f.FullName, f.PostScriptName,
            $"/api/subtitle/font?path={escapedPath}&id={f.Id.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The analysis of a cached script, memoized by its mtime.</summary>
    private async Task<AssAnalyzer.Info?> AnalyzeAsync(string scriptPath, CancellationToken ct)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(scriptPath);
            if (_analyses.TryGetValue(scriptPath, out var memo) && memo.Stamp == stamp) return memo.Info;
            var info = AssAnalyzer.Analyze(await File.ReadAllTextAsync(scriptPath, ct));
            if (_analyses.Count > 2048) _analyses.Clear();
            _analyses[scriptPath] = (stamp, info);
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ASS analysis failed for {Path}", scriptPath);
            return null;
        }
    }
}
