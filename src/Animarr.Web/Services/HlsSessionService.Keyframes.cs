using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Animarr.Web.Services;

// Key-frame-exact stream-copy segmentation (plan task 6.2).
//
// A stream copy can only cut on key frames. The old copy runs used ffmpeg's
// HLS muxer on a 4 s grid: where a GOP is longer than 4 s the muxer skips a
// boundary and its numbering runs behind the playlist's — the content of
// seg-N is not what the playlist says, and at the end the last announced
// segments never exist (E04: key frames at 0 / 10 s → seg-0 and seg-1 under a
// 5-segment playlist; hls.js died with a fatal fragLoadError at 19.7 s).
//
// Here the playlist is built from the file's key-frame index (KeyframeIndex:
// Matroska Cues / MP4 stss / an ffprobe scan): every segment starts ON a key
// frame — the first one at least ~4 s after the previous segment's start
// (merging short GOPs, a long GOP becomes one long segment) — and ffmpeg's
// segment muxer cuts at exactly those times (-segment_times, relative to the
// run's first video packet, which is that segment's key frame). What ffmpeg
// writes is what the playlist announces, for TS and fMP4 copies alike, from
// the start, on resume and after any seek-restart (a restart begins on a
// segment's own key frame, so its cuts are the same ones). The segment muxer
// has no temp-file mode, so it writes raw-NNNNN.* files plus a CSV list it
// appends to as each one closes; FinalizeKeyframeSegments turns listed raw
// files into the seg-NNNNN.* the rest of the session code serves (for fMP4:
// the shared init.mp4 from the first file's ftyp + moov, and each media
// segment = its moof / mdat pairs). Everything downstream — the segment
// endpoint, the produced-top walk, pruning, the throttle — is unchanged.
//
// Gated by Playback:KeyframeHls (AppConfig, live) → AppSettings:KeyframeHls
// (default on). Off, or no index within the start's budget, the old 4 s-grid
// copy runs as before.
public sealed partial class HlsSessionService
{
    /// <summary>AppConfig key (DB, live: PUT /api/app-config/Playback:KeyframeHls
    /// "false" switches new sessions back to the 4 s grid without a restart).</summary>
    public const string KeyframeHlsConfigKey = "Playback:KeyframeHls";

    /// <summary>How long a start waits for the key-frame index. Container
    /// indexes take milliseconds; an ffprobe scan past this keeps running in
    /// the background, this session uses the 4 s grid.</summary>
    private static readonly TimeSpan KeyframeIndexBudget = TimeSpan.FromSeconds(3);

    /// <summary>Most characters of -segment_times per run: Windows caps the whole
    /// command line at 32 767, Linux one argument at 128 KiB. A run whose list
    /// was cut short stops being finalized at its last listed boundary
    /// (<see cref="HlsSession.RunWindowEnd"/>) and a request past it restarts.</summary>
    private static readonly int SegmentTimesBudget = OperatingSystem.IsWindows() ? 20_000 : 120_000;

    private async Task<bool> KeyframeHlsEnabledAsync(CancellationToken ct)
    {
        try
        {
            if (_scopes is not null)
            {
                await using var scope = _scopes.CreateAsyncScope();
                var cfg = scope.ServiceProvider.GetService<IAppConfigService>();
                if (cfg is not null && await cfg.GetAsync(KeyframeHlsConfigKey, ct) is { } v
                    && bool.TryParse(v.Trim(), out var b))
                    return b;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "HLS: {Key} unreadable", KeyframeHlsConfigKey); }
        return _keyframeHlsDefault;
    }

    /// <summary>
    /// The -segment_times list of a key-frame run starting at segment
    /// <paramref name="startSegment"/>: every later cut, relative to that
    /// segment's key frame (the segment muxer measures from the run's first
    /// video packet). Returns the list and the window end — the segment number
    /// the list reaches (SegmentCount when it is complete).
    /// </summary>
    private static (string Times, int WindowEnd) SegmentTimesFor(SegmentLayout layout, int startSegment)
    {
        var cuts = layout.Cuts!;
        var origin = cuts[startSegment];
        var sb = new StringBuilder();
        int n = startSegment + 1;
        for (; n < cuts.Length; n++)
        {
            var t = (cuts[n] - origin).ToString("F3", CultureInfo.InvariantCulture);
            if (sb.Length + t.Length + 1 > SegmentTimesBudget) break;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(t);
        }
        return (sb.ToString(), n);
    }

    /// <summary>
    /// The ffmpeg output part of a key-frame-exact copy run (replaces the HLS
    /// muxer): the segment muxer with explicit cut times. -avoid_negative_ts
    /// disabled keeps the source timestamps exactly (the default shifted a
    /// B-frame file by its decode delay, 60 ms here, and every cut landed one
    /// key frame late). fMP4: fragments on key frames, an empty moov (the
    /// shared init), no trailer, and frag_discont: each file's tfdt is the
    /// absolute decode time of its first sample — without it every file's
    /// tracks restarted at 0 each, which threw away the audio / video offset
    /// within the segment (and any audio sync shift). The edit list it writes
    /// differs between files by well under a millisecond.
    /// </summary>
    private static IEnumerable<string> KeyframeMuxerArgs(HlsPlan plan, string sessionDir, int startSegment,
        string segmentTimes, string listPath)
    {
        bool ts = plan == HlsPlan.TsStreamCopy;
        var a = new List<string> { "-avoid_negative_ts", "disabled", "-f", "segment", "-segment_format", ts ? "mpegts" : "mp4" };
        if (!ts)
            a.AddRange(["-segment_format_options",
                "movflags=+frag_keyframe+empty_moov+default_base_moof+delay_moov+skip_trailer+frag_discont"]);
        if (segmentTimes.Length > 0) a.AddRange(["-segment_times", segmentTimes]);
        a.AddRange([
            // A key frame a hair off its listed time (3-decimal list, a demuxer
            // rounding) still cuts there; neighbouring key frames are always
            // further apart than this.
            "-segment_time_delta", "0.05",
            "-segment_start_number", startSegment.ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "0",
            "-segment_list", FfmpegOutputPath(listPath), "-segment_list_type", "csv",
            FfmpegOutputPath(Path.Combine(sessionDir, $"raw-%05d.{(ts ? "ts" : "mp4")}")),
        ]);
        return a;
    }

    /// <summary>Per-run CSV list path (a restart starts a new one).</summary>
    private static string KeyframeListPath(string sessionDir, int run)
        => Path.Combine(sessionDir, $"_segments-{run.ToString(CultureInfo.InvariantCulture)}.csv");

    /// <summary>
    /// Turns the raw files the current run has closed (its CSV list) into
    /// servable seg-N files. Cheap when nothing changed (one stat). Called from
    /// the throttle tick, the segment wait and the startup wait.
    /// </summary>
    private void FinalizeKeyframeSegments(HlsSession session)
    {
        if (!session.KeyframeMode) return;
        lock (session.FinalizeLock)
        {
            var list = session.KeyframeListPath;
            if (list is null) return;
            long len;
            try { var fi = new FileInfo(list); if (!fi.Exists) return; len = fi.Length; }
            catch { return; }
            if (len == session.KeyframeListRead) return;

            string text;
            try
            {
                using var fs = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Position = session.KeyframeListRead;
                using var sr = new StreamReader(fs, Encoding.UTF8);
                text = sr.ReadToEnd();
            }
            catch { return; }
            // Only whole lines; a half-written last line waits for the next call.
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;
            session.KeyframeListRead += Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNl + 1));

            foreach (var line in text[..(lastNl + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = line.Split(',')[0];
                var n = ParseRawNumber(name);
                if (n < 0) continue;
                var raw = Path.Combine(session.OutputDir, name);
                // Past this run's -segment_times window the muxer just keeps
                // writing into one file — never serve that as seg-N.
                if (n >= session.RunWindowEnd && session.RunWindowEnd < session.SegmentCount)
                {
                    TryDelete(raw);
                    continue;
                }
                try { FinalizeOne(session, raw, n); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HLS {Token}: finalizing {Raw} failed", session.Token, name);
                }
            }
        }
    }

    private void FinalizeOne(HlsSession session, string raw, int n)
    {
        if (!File.Exists(raw)) return;
        if (session.Plan == HlsPlan.TsStreamCopy)
        {
            File.Move(raw, Path.Combine(session.OutputDir, $"seg-{n:D5}.ts"), overwrite: true);
            return;
        }
        FinalizeMp4Raw(session.OutputDir, raw, n);
    }

    /// <summary>
    /// One fMP4 raw file of the segment muxer → seg-N.m4s (from its first
    /// moof / styp / sidx) and, once, init.mp4 (its ftyp + moov).
    /// </summary>
    private static void FinalizeMp4Raw(string dir, string raw, int n)
    {
        var bytes = File.ReadAllBytes(raw);
        int mediaStart = -1, initEnd = -1;
        int pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
            int hdr = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(pos + 8)); hdr = 16; }
            else if (size == 0) size = bytes.Length - pos;
            if (size < hdr || pos + size > bytes.Length) break;
            var type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (type == "moov") initEnd = pos + (int)size;
            if (type is "moof" or "styp" or "sidx" && mediaStart < 0) { mediaStart = pos; break; }
            pos += (int)size;
        }
        if (mediaStart < 0 || initEnd < 0) throw new InvalidDataException($"{Path.GetFileName(raw)}: no moov / moof");
        var init = Path.Combine(dir, "init.mp4");
        if (!File.Exists(init))
            WriteAtomic(init, bytes.AsSpan(0, initEnd));
        WriteAtomic(Path.Combine(dir, $"seg-{n:D5}.m4s"), bytes.AsSpan(mediaStart));
        TryDelete(raw);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> data)
    {
        var tmp = path + ".part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            fs.Write(data);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>raw-NNNNN.{ts|mp4} → NNNNN, else -1.</summary>
    private static int ParseRawNumber(string name)
    {
        if (!name.StartsWith("raw-", StringComparison.Ordinal)) return -1;
        var dot = name.IndexOf('.');
        return dot > 4 && int.TryParse(name.AsSpan(4, dot - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : -1;
    }
}
