using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Animarr.Web.Services;

// Audio as separate HLS renditions (plan §6.4, task 6.3).
//
// With audioRenditions=1 a session's main playlist is video only and every
// audio option — each embedded track, each external dub found next to the
// file — is its own rendition (#EXT-X-MEDIA TYPE=AUDIO, one media playlist
// each), so the player switches audio with hls.audioTrack = N / a Media3
// track selection while the video keeps playing: no restart, no second
// video session. A rendition's ffmpeg starts only when that rendition is
// requested (the default one at the start), reads just its audio, and
// writes a bounded window of segments on the video's segment boundaries
// (the segment muxer with explicit cut times, like the key-frame copies), so
// switching back and forth costs one audio run each, not a re-encode.
// Audio policy (copy / 5.1 / downmix) and the sync offset apply per
// rendition: the plan's baseline (hw / sw channel, the fMP4 copy's reorder
// delay; external dubs use the sw channel) plus the rendition's own offset —
// for the default one the start's audioSyncSec (track memory), for the others
// the client's renditionOffsets, else a dub's DELAY tag.
public sealed partial class HlsSessionService
{
    /// <summary>Segments one audio run writes before it stops (~3 min): a copy
    /// of a lossless track would otherwise fill the session dir to EOF.</summary>
    private const int AudioRunWindowSegments = 45;

    /// <summary>A rendition nobody asked for this long has its ffmpeg stopped
    /// (the player switched away); the next request restarts it.</summary>
    private static readonly TimeSpan AudioRunIdle = TimeSpan.FromSeconds(90);

    /// <summary>One audio rendition of a session.</summary>
    private sealed class AudioRendition
    {
        public required string Id { get; init; }
        public int? EmbeddedIndex { get; init; }
        public string? ExternalPath { get; init; }
        public required string Name { get; set; }
        public string? Language { get; init; }
        public required AudioDecision Audio { get; init; }
        /// <summary>Sync offset of this audio against the video (s, + = later):
        /// the plan's baseline plus the rendition's own.</summary>
        public double OffsetSec { get; init; }
        /// <summary>Added to the input timestamps on top of the offset: an
        /// external dub starts at 0 while the video's packets carry the
        /// source's start time (-copyts).</summary>
        public double InputShiftSec { get; init; }
        /// <summary>The rendition's own sync offset (ms, + = later), without the
        /// plan baseline, and where it came from: "start" (the default
        /// rendition: the start's audioSyncMs / remembered offset),
        /// "request" (renditionOffsets), "memory" (track memory), "delay"
        /// (the dub's DELAY tag), "none".</summary>
        public int SyncMs { get; init; }
        public string SyncSource { get; init; } = "none";
        public bool IsDefault { get; set; }
        public required string Dir { get; init; }
        public required bool Ts { get; init; }

        public readonly SemaphoreSlim RunLock = new(1, 1);
        public readonly object FinalizeLock = new();
        public Process? Proc;
        public int RunStart = -1, RunEnd = -1, RunCount;
        public string? ListPath;
        public long ListRead;
        public DateTime LastRequest = DateTime.UtcNow;
        public int Frontier;
        /// <summary>Output time (s, the segment muxer's) of the first packet past
        /// the last run's window — the CSV entry of its dropped tail — so a copy
        /// run continuing there starts on exactly the next packet.</summary>
        public double SeamSec;
        public int SeamSegment = -1;
        public int LastPruneFloor = int.MinValue;

        public string Ext => Ts ? "ts" : "m4s";
    }

    /// <summary>A rendition as the start response lists it (<c>audioRenditions[]</c>).</summary>
    /// <param name="Id">"a{N}" = embedded audio track N (0:a:N); "x{N}" = external dub N.</param>
    /// <param name="OffsetMs">Total sync offset applied: plan baseline + <paramref name="SyncMs"/>.</param>
    /// <param name="SyncMs">The rendition's own offset (see AudioRendition.SyncSource for <paramref name="SyncSource"/>).</param>
    /// <param name="Uri">Its media playlist, relative to master.m3u8.</param>
    /// <param name="Copied">The audio passes through as is (else transcoded to <paramref name="Codec"/>).</param>
    /// <param name="ReasonCodes">Why this audio isn't the source's own as is —
    /// the audio part of output.transcodeReasonCodes for THIS rendition
    /// (external_audio, audio_unsupported; empty for a copied embedded
    /// track). output.audio* / reasons describe the default rendition at
    /// the start; after a live switch a client takes the Info audio line
    /// (codec, channels, language, copied, these reasons) from the rendition
    /// it plays.</param>
    public sealed record AudioRenditionInfo(string Id, string Kind, int? Index, string? Path, string Name,
        string? Language, string Codec, int Channels, bool Default, int OffsetMs, string Uri,
        int SyncMs = 0, string SyncSource = "none", bool Copied = false, string[]? ReasonCodes = null);

    /// <summary>
    /// The renditions of a start: every embedded audio track plus the external
    /// dubs ExternalTrackService finds (and an explicit dub not among them).
    /// The default is the start's own pick (audioTrackIndex / externalAudio).
    /// </summary>
    /// <param name="baselineSec">The plan's own channel (hw for a re-encode,
    /// sw + reorder delay for the fMP4 copy, 0 for the TS copy) — what the
    /// muxed session would apply to an embedded track.</param>
    /// <param name="swBaselineSec">The channel external dubs ride on (sw +
    /// reorder delay), as in the muxed session.</param>
    private async Task<List<AudioRendition>> BuildRenditionsAsync(string fullPath, ProbeInfo probe, string dir,
        HlsPlan plan, ClientCaps caps, int audioTrackIndex, string? externalAudioPath, double baselineSec,
        double swBaselineSec, double audioSyncSec, IReadOnlyDictionary<string, int>? renditionOffsets,
        CancellationToken ct, IReadOnlyDictionary<string, int>? rememberedOffsets = null)
    {
        bool ts = plan == HlsPlan.TsStreamCopy;
        var output = ts ? AudioOutput.HlsTs : AudioOutput.HlsFmp4;
        var list = new List<AudioRendition>();
        // A rendition's own offset: the default one's is the start's (audioSyncMs
        // or, with usePrefs, its remembered one); any other's the client's
        // renditionOffsets, else its remembered one (track memory, usePrefs),
        // else a dub's DELAY tag.
        (int Ms, string Source) SyncFor(string id, bool isDefault, string memoryKey, int? delayMs)
        {
            if (isDefault) return ((int)Math.Round(audioSyncSec * 1000), "start");
            if (renditionOffsets is not null && renditionOffsets.TryGetValue(id, out var ms))
                return (Math.Clamp(ms, -3000, 3000), "request");
            if (rememberedOffsets is not null && rememberedOffsets.TryGetValue(memoryKey, out var mem)) return (mem, "memory");
            return delayMs is int dl && dl != 0 ? (Math.Clamp(dl, -3000, 3000), "delay") : (0, "none");
        }

        for (int i = 0; i < probe.AudioStreams.Count; i++)
        {
            var a = probe.AudioStreams[i];
            var id = "a" + i.ToString(CultureInfo.InvariantCulture);
            bool isDefault = externalAudioPath is null && i == audioTrackIndex;
            var decision = AudioPolicy.Decide(a.Codec, a.Channels, a.Profile, caps, output);
            var sync = SyncFor(id, isDefault,
                TrackPreferenceService.AudioOffsetKey(TrackPreferenceService.KindEmbedded, a.Language, null), null);
            list.Add(new AudioRendition
            {
                Id = id, EmbeddedIndex = i, Language = a.Language, Audio = decision, IsDefault = isDefault,
                Name = RenditionName(i + 1, a.Language, a.Title, decision), Dir = Path.Combine(dir, "audio", id), Ts = ts,
                OffsetSec = baselineSec + sync.Ms / 1000.0, SyncMs = sync.Ms, SyncSource = sync.Source,
            });
        }

        // External dubs next to the file (ExternalTrackService is scoped).
        var dubs = new List<(string Path, string? Language, string? Label, string? Codec, int? Channels, int? DelayMs, double? Dur, string? GroupKey)>();
        try
        {
            if (_scopes is not null)
            {
                await using var scope = _scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ExternalTrackService>() is { } ext)
                    foreach (var t in await ext.FindForVideoAsync(fullPath, probe: true, ct))
                        // A dub ffprobe can't read (Codec null) can't be played
                        // either — its rendition would only ever answer 503.
                        if (t.Kind == "audio" && t.Codec is not null)
                            dubs.Add((t.Path, t.Language, t.Label, t.Codec, t.Channels, t.DelayMs, t.DurationSec, t.GroupKey));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "HLS renditions: external dubs of {Path} unavailable", fullPath); }
        if (externalAudioPath is not null
            && !dubs.Any(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(externalAudioPath), StringComparison.OrdinalIgnoreCase)))
            dubs.Insert(0, (externalAudioPath, null, Path.GetFileNameWithoutExtension(externalAudioPath), null, null, null, null, null));
        for (int n = 0; n < dubs.Count; n++)
        {
            var d = dubs[n];
            var id = "x" + n.ToString(CultureInfo.InvariantCulture);
            bool isDefault = externalAudioPath is not null
                && string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(externalAudioPath), StringComparison.OrdinalIgnoreCase);
            // A dub is always transcoded in the muxed plans; as its own
            // rendition its probed codec is known, so the policy can copy it —
            // unless it is shorter than the video (or its length is unknown):
            // a copy can't be padded, and a rendition that runs out of packets
            // stalls the player at its end. Transcoded, it gets silence (apad).
            bool coversVideo = d.Dur is double dd && dd >= probe.DurationSec - 0.5;
            var decision = AudioPolicy.Decide(coversVideo ? d.Codec : null, d.Channels ?? 2, null, caps, output);
            var sync = SyncFor(id, isDefault,
                TrackPreferenceService.AudioOffsetKey(TrackPreferenceService.KindExternal, d.Language, d.GroupKey), d.DelayMs);
            list.Add(new AudioRendition
            {
                Id = id, ExternalPath = d.Path, Language = d.Language, Audio = decision, IsDefault = isDefault,
                Name = (d.Label ?? Path.GetFileNameWithoutExtension(d.Path)) + " · " + DescribeAudio(decision),
                Dir = Path.Combine(dir, "audio", id), Ts = ts,
                OffsetSec = swBaselineSec + sync.Ms / 1000.0, SyncMs = sync.Ms, SyncSource = sync.Source,
                InputShiftSec = probe.StartTimeSec,
            });
        }
        if (list.Count > 0 && !list.Any(r => r.IsDefault)) list[0].IsDefault = true;
        // NAMEs must be unique within the group.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            var name = r.Name;
            for (int k = 2; !seen.Add(name); k++) name = $"{r.Name} ({k})";
            r.Name = name;
        }
        return list;
    }

    private static string RenditionName(int number, string? language, string? title, AudioDecision a)
    {
        var lang = string.IsNullOrWhiteSpace(language) || language == "und" ? null : language;
        var head = title is { Length: > 0 } t ? (lang is null ? t : $"{lang} · {t}") : lang ?? $"Track {number}";
        return $"{number}. {head} · {DescribeAudio(a)}";
    }

    private static string DescribeAudio(AudioDecision a)
    {
        var codec = a.Codec switch
        {
            "aac" => "AAC", "ac3" => "AC-3", "eac3" => "E-AC-3", "dts" => "DTS", "mp3" => "MP3", "mp2" => "MP2",
            "opus" => "Opus", "flac" => "FLAC", var c => c.ToUpperInvariant(),
        };
        var ch = a.Channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", var n => n + "ch" };
        return $"{codec} {ch}";
    }

    /// <summary>
    /// master.m3u8 with audio renditions: one GROUP-ID "aud", an EXT-X-MEDIA
    /// per rendition (NAME, LANGUAGE, DEFAULT / AUTOSELECT, CHANNELS, URI) and
    /// the video variant pointing at it (AUDIO="aud"); CODECS lists the video
    /// codec and every rendition's audio codec, as the spec asks.
    /// </summary>
    private static string BuildMasterPlaylistWithAudio(ProbeInfo p, long bandwidthBps, string videoCodecs,
        IReadOnlyList<AudioRendition> renditions)
    {
        var bandwidth = bandwidthBps > 0 ? bandwidthBps : EstimatedBandwidth(p.Width, p.Height);
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:7\n");
        foreach (var r in renditions)
        {
            sb.Append("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"").Append(PlaylistText(r.Name)).Append('"');
            if (PlaylistLanguage(r.Language) is { } lang)
                sb.Append(",LANGUAGE=\"").Append(lang).Append('"');
            sb.Append(r.IsDefault ? ",DEFAULT=YES" : ",DEFAULT=NO").Append(",AUTOSELECT=YES");
            sb.Append(",CHANNELS=\"").Append(r.Audio.Channels.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",URI=\"audio/").Append(r.Id).Append("/media.m3u8\"\n");
        }
        var codecs = string.Join(',', new[] { videoCodecs }.Concat(renditions.Select(r => r.Audio.CodecsTag)).Distinct());
        sb.Append($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={p.Width}x{p.Height},CODECS=\"{codecs}\",AUDIO=\"aud\"\n");
        sb.Append("media.m3u8\n");
        return sb.ToString();
    }

    /// <summary>
    /// A container tag / file name as a quoted playlist attribute value:
    /// control characters (CR / LF would start a new playlist line — a tag
    /// could inject #EXT-X-… lines) dropped, double quotes made single,
    /// at most 128 characters.
    /// </summary>
    internal static string PlaylistText(string? text)
    {
        var s = new string((text ?? "").Where(ch => !char.IsControl(ch) && ch != '\u2028' && ch != '\u2029')
            .Select(ch => ch == '"' ? '\'' : ch).Take(128).ToArray()).Trim();
        return s.Length == 0 ? "Audio" : s;
    }

    /// <summary>A language tag fit for LANGUAGE= (<c>[A-Za-z-]{1,16}</c>, not
    /// "und"); null otherwise.</summary>
    internal static string? PlaylistLanguage(string? language)
        => language is { Length: >= 1 and <= 16 } l && l != "und"
           && l.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-') ? l : null;

    /// <summary>The start response's rendition list.</summary>
    private static List<AudioRenditionInfo> RenditionInfos(IEnumerable<AudioRendition> renditions)
        => renditions.Select(r => new AudioRenditionInfo(r.Id, r.EmbeddedIndex is not null ? "embedded" : "external",
            r.EmbeddedIndex, r.ExternalPath, r.Name, r.Language, r.Audio.Codec, r.Audio.Channels, r.IsDefault,
            (int)Math.Round(r.OffsetSec * 1000), $"audio/{r.Id}/media.m3u8", r.SyncMs, r.SyncSource,
            r.Audio.Copy,
            r.ExternalPath is not null ? [ReasonCodes.ExternalAudio]
            : r.Audio.Copy ? [] : [ReasonCodes.AudioUnsupported])).ToList();

    /// <summary>
    /// Writes the rendition's media playlist: the video's segments (same
    /// starts, same EXTINFs) and the same discontinuities — every boundary for
    /// fMP4, the video's restart points for TS — so a player's discontinuity
    /// counters line up across the group.
    /// </summary>
    private static void WriteRenditionPlaylist(SegmentLayout layout, IReadOnlyCollection<int>? boundaries, AudioRendition r)
    {
        Directory.CreateDirectory(r.Dir);
        var path = Path.Combine(r.Dir, "media.m3u8");
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, BuildMediaPlaylist(layout, r.Ts ? HlsPlan.TsStreamCopy : HlsPlan.Fmp4StreamCopy,
            restartBoundaries: r.Ts ? boundaries : null), Encoding.ASCII);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// GET /api/hls/{token}/audio/{id}/{file}: the rendition's playlist, its
    /// init.mp4 or segment N — starting (or moving) its ffmpeg run when the
    /// file isn't there yet. null = unknown session / rendition, or not
    /// produced in time.
    /// </summary>
    public async Task<string?> WaitForRenditionFileAsync(string token, string id, string fileName, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(token, out var session) || session.Renditions is null || session.Stopped) return null;
        var r = session.Renditions.FirstOrDefault(x => x.Id == id);
        if (r is null) return null;
        session.Touch();
        r.LastRequest = DateTime.UtcNow;
        var full = Path.Combine(r.Dir, fileName);
        if (fileName == "media.m3u8")
        {
            try { WriteRenditionPlaylist(session.Layout, session.GetRestartBoundaries(), r); }
            catch (Exception ex) { _logger.LogDebug(ex, "HLS {Token}: rendition {Id} playlist rewrite failed", token, r.Id); }
            return File.Exists(full) ? full : null;
        }

        int target;
        if (fileName == "init.mp4")
        {
            if (r.Ts) return null;
            if (File.Exists(full)) return full;
            target = r.Proc is null ? Math.Clamp(session.PlayFrontier, 0, Math.Max(0, session.SegmentCount - 1)) : r.RunStart;
        }
        else
        {
            target = ParseSegmentNumber(fileName);
            if (target < 0 || target >= session.SegmentCount || !fileName.EndsWith("." + r.Ext, StringComparison.Ordinal))
                return null;
            if (target > r.Frontier) r.Frontier = target;
            FinalizeRendition(r);
            if (File.Exists(full)) { PruneRendition(r); return full; }
        }

        await EnsureRenditionRunAsync(session, r, target, ct);
        var deadline = DateTime.UtcNow + SegmentWaitTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            FinalizeRendition(r);
            if (File.Exists(full)) return full;
            var p = r.Proc;
            if (p is null || (p.HasExited && SafeExitCode(p) != 0)) return null;
            if (p.HasExited)
            {
                FinalizeRendition(r);
                return File.Exists(full) ? full : null;
            }
            await Task.Delay(100, ct);
        }
        return null;
    }

    /// <summary>
    /// Makes sure a run of <paramref name="r"/> will write segment
    /// <paramref name="target"/>: keeps the current one when the target lies
    /// ahead within reach of it, else (re)starts at the target.
    /// </summary>
    private async Task EnsureRenditionRunAsync(HlsSession session, AudioRendition r, int target, CancellationToken ct)
    {
        await r.RunLock.WaitAsync(ct);
        try
        {
            if (session.Stopped) return;
            var p = r.Proc;
            int top = RenditionTop(r);
            bool keep = p is not null && !p.HasExited && target >= r.RunStart && target < r.RunEnd
                        && target <= top + RestartThresholdSegments;
            if (keep) return;
            if (p is not null && p.HasExited && SafeExitCode(p) == 0 && target >= r.RunStart && target <= top) return;

            try { if (p is not null && !p.HasExited) p.Kill(true); } catch { }
            if (p is not null)
            {
                try { using var c = new CancellationTokenSource(2000); await p.WaitForExitAsync(c.Token); } catch { }
                try { p.Dispose(); } catch { }
            }
            // Stopped while the old run was being killed: no new run, and no
            // CreateDirectory bringing the deleted session dir back.
            if (session.Stopped) return;
            Directory.CreateDirectory(r.Dir);
            foreach (var f in Directory.EnumerateFiles(r.Dir, "raw-*")) TryDelete(f);

            int end = Math.Min(session.SegmentCount, target + AudioRunWindowSegments);
            var listPath = Path.Combine(r.Dir, $"_segments-{r.RunCount.ToString(CultureInfo.InvariantCulture)}.csv");
            var args = BuildRenditionArgs(session, r, target, end, listPath,
                seamOutSec: r.SeamSegment == target ? r.SeamSec : null);
            var (proc, _) = LaunchFfmpeg(args, r.Dir, session.Token + "/" + r.Id, target, tail: null);
            if (session.Stopped)
            {
                // Stop ran between the check and the launch (it saw the old
                // process, not this one): this run dies here.
                try { proc.Kill(true); } catch { }
                return;
            }
            lock (r.FinalizeLock)
            {
                r.Proc = proc;
                r.RunStart = target;
                r.RunEnd = end;
                r.ListPath = listPath;
                r.ListRead = 0;
                r.RunCount++;
                // A run started ahead of the player (the prefetch) keeps the
                // player's frontier; a seek moves it.
                if (target < r.Frontier || target > r.Frontier + RestartThresholdSegments)
                {
                    r.Frontier = target;
                    r.LastPruneFloor = int.MinValue;
                }
            }
        }
        finally { r.RunLock.Release(); }
    }

    /// <summary>
    /// ffmpeg for one audio run: just the rendition's audio from its source,
    /// shifted by its offset, from segment <paramref name="start"/> to
    /// <paramref name="end"/> (exclusive), cut by the segment muxer on the
    /// video's segment starts.
    ///
    /// The segment muxer's cut times are relative to the run's FIRST packet,
    /// so that packet has to sit where the run is meant to begin
    /// (measured, ffmpeg 8): a transcode trims at the input -ss on its own,
    /// but a stream copy starts wherever the demuxer seek lands — in a
    /// Matroska file the video key frame before the seek point, up to a GOP
    /// early — so the copy drops what lies before it itself (noise bsf). The
    /// offset is an output shift (-output_ts_offset): -itsoffset moved the
    /// timestamps but not the transcode's trim point, which then cut at the
    /// wrong place by the offset.
    /// </summary>
    private static List<string> BuildRenditionArgs(HlsSession session, AudioRendition r, int start, int end, string listPath,
        double? seamOutSec = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var layout = session.Layout;
        double from = layout.Start(start);
        double d = r.OffsetSec;
        double shift = d + r.InputShiftSec;
        double videoStart = session.Probe?.StartTimeSec ?? 0;
        // Packet times inside ffmpeg are the input's own (-copyts): the
        // source's start time included for an embedded track; a dub starts at 0.
        double inputStart = r.ExternalPath is null ? videoStart : 0;
        // Output time t carries source time t - d: read from there (never
        // before 0), so the first packet lands at `from` (or at d).
        double srcStart = Math.Max(0, from - d);
        double outFirst = srcStart + d;
        // A copy continuing the previous window starts on the packet that
        // window's last cut left over (its CSV), not a frame or two off it —
        // the copy can only cut on its packets, so `from` itself would leave a
        // gap of up to two frames at every seam.
        double? copyFrom = null;
        if (r.Audio.Copy && seamOutSec is double seam)
        {
            copyFrom = seam - shift;                  // that packet's input time
            outFirst = seam - videoStart;             // and its timeline time
            srcStart = Math.Max(0, copyFrom.Value - inputStart);
        }
        else if (r.Audio.Copy && srcStart > 0) copyFrom = srcStart + inputStart;
        // The last window ends with the video: a longer dub is cut there, a
        // shorter one (transcoded) padded up to it.
        double endOut = end < layout.Count ? layout.Start(end) : session.TotalDurationSec;
        double runDur = Math.Max(0.1, endOut - outFirst + (end < layout.Count ? 0.2 : 0));

        // -loglevel error: the segment muxer's mpegts sub-muxer warns "frame
        // size not set" for every segment of a transcoded track.
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (srcStart > 0) a.AddRange(["-ss", srcStart.ToString("F3", inv)]);
        a.AddRange(["-t", runDur.ToString("F3", inv)]);
        a.AddRange(["-i", r.ExternalPath ?? session.SourcePath, "-copyts"]);
        if (Math.Abs(shift) >= 0.0005) a.AddRange(["-output_ts_offset", shift.ToString("F3", inv)]);
        a.AddRange(["-map", r.ExternalPath is not null ? "0:a:0" : $"0:a:{r.EmbeddedIndex!.Value.ToString(inv)}",
                    "-vn", "-sn", "-dn"]);
        if (copyFrom is double cf && cf > 0)
            a.AddRange(["-bsf:a", $"noise=drop=lt(pts*tb\\,{(cf - 0.0005).ToString("F4", inv)})"]);
        // A transcoded track is padded with silence to the window's end: an
        // audio that ends early (a short dub) still fills every segment.
        a.AddRange(r.Audio.Copy ? r.Audio.Args() : WithPad(r.Audio.Args().ToArray(), runDur));
        MergeBitstreamFilters(a);
        a.AddRange(["-avoid_negative_ts", "disabled", "-f", "segment", "-segment_format", r.Ts ? "mpegts" : "mp4"]);
        if (!r.Ts)
            a.AddRange(["-segment_format_options",
                "movflags=+empty_moov+default_base_moof+delay_moov+skip_trailer+frag_discont:frag_duration=1000000"]);
        // A cut at the window's end too: the -t slack past it lands in raw-{end},
        // which the finalize pass drops, instead of overlapping the next run.
        var times = new StringBuilder();
        for (int i = start + 1; i <= Math.Min(end, layout.Count - 1); i++)
        {
            if (times.Length > 0) times.Append(',');
            times.Append(Math.Max(0, layout.Start(i) - outFirst).ToString("F3", inv));
        }
        if (times.Length > 0) a.AddRange(["-segment_times", times.ToString()]);
        a.AddRange([
            "-segment_time_delta", "0.03",
            "-segment_start_number", start.ToString(inv),
            "-reset_timestamps", "0",
            "-segment_list", FfmpegOutputPath(listPath), "-segment_list_type", "csv",
            FfmpegOutputPath(Path.Combine(r.Dir, $"raw-%05d.{(r.Ts ? "ts" : "mp4")}")),
        ]);
        return a;
    }

    /// <summary>Listed raw files of the current run → seg-N (+ init.mp4).</summary>
    private void FinalizeRendition(AudioRendition r)
    {
        lock (r.FinalizeLock)
        {
            var list = r.ListPath;
            if (list is null) return;
            long len;
            try { var fi = new FileInfo(list); if (!fi.Exists) return; len = fi.Length; } catch { return; }
            if (len == r.ListRead) return;
            string text;
            try
            {
                using var fs = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Position = r.ListRead;
                using var sr = new StreamReader(fs, Encoding.UTF8);
                text = sr.ReadToEnd();
            }
            catch { return; }
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;
            r.ListRead += Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNl + 1));
            foreach (var line in text[..(lastNl + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(',');
                var name = fields[0];
                var n = ParseRawNumber(name);
                if (n < 0) continue;
                var raw = Path.Combine(r.Dir, name);
                if (n == r.RunEnd && fields.Length > 1
                    && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seamSec))
                    (r.SeamSec, r.SeamSegment) = (seamSec, n);
                // The -t tail past the window; a segment an earlier run already
                // wrote (same cuts, same offset — and it may be being served).
                if (n >= r.RunEnd || File.Exists(Path.Combine(r.Dir, $"seg-{n:D5}.{r.Ext}"))) { TryDelete(raw); continue; }
                try
                {
                    if (r.Ts) File.Move(raw, Path.Combine(r.Dir, $"seg-{n:D5}.ts"), overwrite: true);
                    else FinalizeMp4Raw(r.Dir, raw, n);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "HLS rendition {Id}: finalizing {Raw} failed", r.Id, name); }
            }
        }
    }

    /// <summary>Highest contiguous finalized segment of the current run.</summary>
    private static int RenditionTop(AudioRendition r)
    {
        if (r.RunStart < 0) return -1;
        int n = r.RunStart - 1;
        while (File.Exists(Path.Combine(r.Dir, $"seg-{n + 1:D5}.{r.Ext}"))) n++;
        return n;
    }

    private static void PruneRendition(AudioRendition r)
    {
        var floor = r.Frontier - PruneKeepBehindSegments;
        if (floor <= r.LastPruneFloor) return;
        if (PruneBelow(r.Dir, floor)) r.LastPruneFloor = floor;
    }

    /// <summary>A finished run's successor starts this many segments before
    /// the player reaches its end, so the next window is there in time.</summary>
    private const int AudioRunPrefetchSegments = 15;

    /// <summary>Throttle-tick housekeeping: finalize; start the next window of
    /// a rendition being played; stop the runs of renditions the player
    /// stopped asking for (switched away).</summary>
    private void TickRenditions(HlsSession session)
    {
        if (session.Renditions is null) return;
        foreach (var r in session.Renditions)
        {
            FinalizeRendition(r);
            var p = r.Proc;
            if (p is null) continue;
            bool idle = DateTime.UtcNow - r.LastRequest > AudioRunIdle;
            if (!p.HasExited && idle && r.RunLock.Wait(0))
            {
                try
                {
                    try { p.Kill(true); } catch { }
                    _logger.LogDebug("HLS {Token}: rendition {Id} idle — its ffmpeg stopped", session.Token, r.Id);
                }
                finally { r.RunLock.Release(); }
            }
            else if (p.HasExited && SafeExitCode(p) == 0 && !idle && r.RunEnd < session.SegmentCount
                     && r.Frontier >= r.RunEnd - AudioRunPrefetchSegments && r.RunLock.CurrentCount > 0)
            {
                var next = r.RunEnd;
                _ = EnsureRenditionRunAsync(session, r, next, CancellationToken.None).ContinueWith(
                    t => _logger.LogWarning(t.Exception, "HLS {Token}: rendition {Id} prefetch at seg-{Seg} failed",
                        session.Token, r.Id, next),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>Kills the renditions' ffmpeg runs (Stop) and returns them so
    /// the caller can wait for them before deleting the session dir.</summary>
    private static List<Process> StopRenditions(HlsSession session)
    {
        var procs = new List<Process>();
        if (session.Renditions is null) return procs;
        foreach (var r in session.Renditions)
        {
            if (r.Proc is not { } p) continue;
            try { if (!p.HasExited) p.Kill(true); } catch { }
            procs.Add(p);
        }
        return procs;
    }

    /// <summary>
    /// Whether a native start (Direct Play / Direct Stream) would lose audio
    /// choices a client asked to switch live (audioRenditions=1): more than one
    /// embedded track, or any external dub. <paramref name="localTracks"/> = the
    /// client switches embedded tracks itself (the TV on Direct Play) — then
    /// only dubs count.
    /// </summary>
    public async Task<bool> HasAudioChoicesAsync(string fullPath, bool localTracks, CancellationToken ct)
    {
        var probe = await ProbeMediaAsync(fullPath, ct);
        if (!localTracks && (probe?.AudioStreams.Count ?? 0) > 1) return true;
        if (_scopes is null) return false;
        await using var scope = _scopes.CreateAsyncScope();
        return scope.ServiceProvider.GetService<ExternalTrackService>() is { } ext
               && (await ext.FindForVideoAsync(fullPath, probe: false, ct)).Any(t => t.Kind == "audio");
    }

    /// <summary>"a1:300,x0:-150" → { a1: 300, x0: -150 } (bad pairs skipped).</summary>
    public static IReadOnlyDictionary<string, int>? ParseRenditionOffsets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = pair.IndexOf(':');
            if (c <= 0) continue;
            if (int.TryParse(pair.AsSpan(c + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                d[pair[..c]] = ms;
        }
        return d.Count == 0 ? null : d;
    }
}
