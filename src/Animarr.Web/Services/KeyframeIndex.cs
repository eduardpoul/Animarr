using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Animarr.Web.Services;

/// <summary>
/// Presentation times (seconds) of the main video stream's key frames, sorted.
/// <see cref="Source"/> says where they came from: "cues" (Matroska Cues),
/// "stss" (MP4 sync-sample table) or "packets" (an ffprobe scan).
/// </summary>
public sealed record KeyframeIndex(double[] Times, string Source);

/// <summary>
/// Key-frame index per file version — what the key-frame-exact HLS copy
/// (plan task 6.2) lays its segments on: a stream copy can only cut on key
/// frames, so a playlist on a fixed 4 s grid announced segments a sparse-GOP
/// file never produced.
///
/// Read cheaply from the container's own index where there is one — the
/// Matroska Cues (mkvmerge and ffmpeg write a cue for every video key frame)
/// or the MP4 sync-sample table — which costs a few KB of reads, not a pass
/// over the file. Anything else (MPEG-TS, AVI, a Matroska without Cues, a
/// fragmented MP4) falls back to an ffprobe packet scan, which reads the whole
/// file: it gets a time budget per call and keeps running in the background
/// past it, so a later playback of the same file finds it ready. Results are
/// kept in memory and on disk (the cache root), keyed by path + mtime + size,
/// so a replaced file is simply a new key.
/// </summary>
public sealed class KeyframeIndexService
{
    private const int MaxEntries = 256;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger<KeyframeIndexService> _logger;
    private readonly string? _diskDir;
    private readonly ConcurrentDictionary<string, Lazy<Task<KeyframeIndex?>>> _slots = new();

    public KeyframeIndexService(ILogger<KeyframeIndexService> logger, MediaCachePaths? paths = null)
    {
        _logger = logger;
        _diskDir = paths is null ? null : Path.Combine(paths.CacheRoot, "keyframes");
    }

    /// <summary>
    /// The key-frame index of <paramref name="fullPath"/>'s video stream
    /// <paramref name="videoStreamIndex"/> (absolute ffprobe index, -1 = the
    /// first video), or null when it isn't known within <paramref name="budget"/>
    /// (a scan still running carries on in the background) or can't be built.
    /// </summary>
    public async Task<KeyframeIndex?> GetAsync(string fullPath, int videoStreamIndex, TimeSpan budget,
        CancellationToken ct)
    {
        FileInfo fi;
        try { fi = new FileInfo(fullPath); if (!fi.Exists) return null; }
        catch { return null; }
        var key = $"{fullPath}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{videoStreamIndex}|kf1";
        if (_slots.Count > MaxEntries) _slots.Clear();
        var slot = _slots.GetOrAdd(key, k => new Lazy<Task<KeyframeIndex?>>(() => Task.Run(() => BuildAsync(fullPath, videoStreamIndex, k))));
        var task = slot.Value;
        try
        {
            var done = await Task.WhenAny(task, Task.Delay(budget, ct));
            if (done != task) return null;
            var result = await task;
            if (result is null) _slots.TryRemove(key, out _);   // retry next time
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Key frame index failed for {Path}", fullPath);
            _slots.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<KeyframeIndex?> BuildAsync(string fullPath, int videoStreamIndex, string key)
    {
        var diskPath = DiskPath(key);
        if (diskPath is not null && TryLoad(diskPath) is { } cached) return cached;

        KeyframeIndex? index = null;
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        try
        {
            index = ext switch
            {
                ".mkv" or ".mk3d" or ".webm" => MatroskaCues.Read(fullPath, videoStreamIndex),
                ".mp4" or ".m4v" or ".mov" => Mp4SyncSamples.Read(fullPath, videoStreamIndex),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container key frame index unreadable for {Path}", fullPath);
        }
        index ??= await ScanAsync(fullPath, videoStreamIndex);
        if (index is not null)
        {
            _logger.LogInformation("Key frame index of {Path}: {Count} key frames from {Source}",
                fullPath, index.Times.Length, index.Source);
            if (diskPath is not null) TrySave(diskPath, index);
        }
        return index;
    }

    /// <summary>ffprobe packet scan of the video stream (reads the whole file).</summary>
    private async Task<KeyframeIndex?> ScanAsync(string fullPath, int videoStreamIndex)
    {
        try
        {
            var sel = videoStreamIndex >= 0 ? videoStreamIndex.ToString(CultureInfo.InvariantCulture) : "V:0";
            var r = await FfprobeRunner.RunAsync(
                ["-v", "error", "-select_streams", sel, "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", fullPath],
                ScanTimeout, CancellationToken.None);
            if (r.ExitCode != 0) return null;
            var times = new List<double>();
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = line.IndexOf(',');
                if (c <= 0 || line.IndexOf('K', c) < 0) continue;
                if (double.TryParse(line.AsSpan(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    times.Add(t);
            }
            return Normalize(times, "packets");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Key frame scan failed for {Path}", fullPath);
            return null;
        }
    }

    /// <summary>Sorted, de-duplicated, non-negative; null when empty.</summary>
    internal static KeyframeIndex? Normalize(List<double> times, string source)
    {
        if (times.Count == 0) return null;
        times.Sort();
        var outList = new List<double>(times.Count);
        foreach (var t in times)
        {
            if (double.IsNaN(t) || t < -1) continue;
            if (outList.Count > 0 && t - outList[^1] < 0.0005) continue;
            outList.Add(Math.Max(0, t));
        }
        return outList.Count == 0 ? null : new KeyframeIndex(outList.ToArray(), source);
    }

    private string? DiskPath(string key)
    {
        if (_diskDir is null) return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_diskDir, hash + ".txt");
    }

    private static KeyframeIndex? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return null;
            var times = new List<double>(lines.Length);
            for (int i = 1; i < lines.Length; i++)
                if (double.TryParse(lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) times.Add(t);
            return Normalize(times, lines[0]);
        }
        catch { return null; }
    }

    private void TrySave(string path, KeyframeIndex index)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder(index.Times.Length * 12);
            sb.Append(index.Source).Append('\n');
            foreach (var t in index.Times) sb.Append(t.ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Key frame index not cached on disk"); }
    }
}

/// <summary>
/// Key frames from a Matroska file's Cues: the cue points of the video track
/// (mkvmerge and ffmpeg's muxer write one per video key frame). Reads the EBML
/// header, the SeekHead / Info / Tracks and the Cues element only — never the
/// Clusters. null when the file has no usable Cues.
/// </summary>
internal static class MatroskaCues
{
    private const uint IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdSeek = 0x4DBB, IdSeekId = 0x53AB,
        IdSeekPosition = 0x53AC, IdInfo = 0x1549A966, IdTimestampScale = 0x2AD7B1, IdTracks = 0x1654AE6B,
        IdTrackEntry = 0xAE, IdTrackNumber = 0xD7, IdTrackType = 0x83, IdCues = 0x1C53BB6B, IdCuePoint = 0xBB,
        IdCueTime = 0xB3, IdCueTrackPositions = 0xB7, IdCueTrack = 0xF7, IdCluster = 0x1F43B675;

    public static KeyframeIndex? Read(string path, int videoStreamIndex)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        // EBML header, then the Segment.
        if (ReadId(fs) != 0x1A45DFA3) return null;
        var hdrSize = ReadSize(fs, out _);
        fs.Seek(hdrSize, SeekOrigin.Current);
        if (ReadId(fs) != IdSegment) return null;
        ReadSize(fs, out _);
        long segStart = fs.Position;

        long cuesPos = -1, infoPos = -1, tracksPos = -1;
        // Walk the Segment's top-level elements up to the first Cluster.
        for (int guard = 0; guard < 64 && fs.Position < fs.Length; guard++)
        {
            long elemPos = fs.Position;
            uint id = ReadId(fs);
            long size = ReadSize(fs, out bool unknown);
            long dataPos = fs.Position;
            if (id == 0) return null;
            if (id == IdSeekHead && !unknown)
            {
                foreach (var (seekId, pos) in ReadSeekHead(fs, dataPos + size))
                {
                    if (seekId == IdCues) cuesPos = segStart + pos;
                    else if (seekId == IdInfo) infoPos = segStart + pos;
                    else if (seekId == IdTracks) tracksPos = segStart + pos;
                }
            }
            else if (id == IdInfo) infoPos = elemPos;
            else if (id == IdTracks) tracksPos = elemPos;
            else if (id == IdCues) cuesPos = elemPos;
            if (id == IdCluster || unknown) break;
            fs.Position = dataPos + size;
        }
        if (cuesPos < 0) return null;

        double scale = 1_000_000;   // TimestampScale default: 1 ms
        if (infoPos >= 0)
        {
            fs.Position = infoPos;
            if (ReadId(fs) == IdInfo)
            {
                long size = ReadSize(fs, out _), end = fs.Position + size;
                while (fs.Position < end)
                {
                    uint id = ReadId(fs); long s = ReadSize(fs, out _);
                    if (id == IdTimestampScale) scale = ReadUInt(fs, s); else fs.Position += s;
                }
            }
        }

        // The video track's number: TrackEntry #videoStreamIndex (ffprobe
        // numbers Matroska streams in TrackEntry order), else the first video.
        long videoTrack = -1, firstVideo = -1;
        if (tracksPos >= 0)
        {
            fs.Position = tracksPos;
            if (ReadId(fs) == IdTracks)
            {
                long size = ReadSize(fs, out _), end = fs.Position + size;
                int ordinal = 0;
                while (fs.Position < end)
                {
                    uint id = ReadId(fs); long s = ReadSize(fs, out _); long next = fs.Position + s;
                    if (id == IdTrackEntry)
                    {
                        long number = -1, type = -1;
                        while (fs.Position < next)
                        {
                            uint cid = ReadId(fs); long cs = ReadSize(fs, out _);
                            if (cid == IdTrackNumber) number = ReadUInt(fs, cs);
                            else if (cid == IdTrackType) type = ReadUInt(fs, cs);
                            else fs.Position += cs;
                        }
                        if (type == 1)
                        {
                            if (firstVideo < 0) firstVideo = number;
                            if (ordinal == videoStreamIndex) videoTrack = number;
                        }
                        ordinal++;
                    }
                    fs.Position = next;
                }
            }
        }
        if (videoTrack < 0) videoTrack = firstVideo;

        fs.Position = cuesPos;
        if (ReadId(fs) != IdCues) return null;
        long cuesSize = ReadSize(fs, out _), cuesEnd = fs.Position + cuesSize;
        var times = new List<double>();
        while (fs.Position < cuesEnd)
        {
            uint id = ReadId(fs); long s = ReadSize(fs, out _); long next = fs.Position + s;
            if (id == IdCuePoint)
            {
                long time = -1; bool ours = false;
                while (fs.Position < next)
                {
                    uint cid = ReadId(fs); long cs = ReadSize(fs, out _); long cnext = fs.Position + cs;
                    if (cid == IdCueTime) time = ReadUInt(fs, cs);
                    else if (cid == IdCueTrackPositions)
                    {
                        while (fs.Position < cnext)
                        {
                            uint pid = ReadId(fs); long ps = ReadSize(fs, out _);
                            if (pid == IdCueTrack) { if (videoTrack < 0 || ReadUInt(fs, ps) == videoTrack) ours = true; }
                            else fs.Position += ps;
                        }
                    }
                    fs.Position = cnext;
                }
                if (time >= 0 && ours) times.Add(time * scale / 1e9);
            }
            fs.Position = next;
        }
        return KeyframeIndexService.Normalize(times, "cues");
    }

    private static IEnumerable<(uint Id, long Pos)> ReadSeekHead(FileStream fs, long end)
    {
        var list = new List<(uint, long)>();
        while (fs.Position < end)
        {
            uint id = ReadId(fs); long s = ReadSize(fs, out _); long next = fs.Position + s;
            if (id == IdSeek)
            {
                uint seekId = 0; long pos = -1;
                while (fs.Position < next)
                {
                    uint cid = ReadId(fs); long cs = ReadSize(fs, out _);
                    if (cid == IdSeekId) seekId = (uint)ReadUInt(fs, cs);
                    else if (cid == IdSeekPosition) pos = ReadUInt(fs, cs);
                    else fs.Position += cs;
                }
                if (seekId != 0 && pos >= 0) list.Add((seekId, pos));
            }
            fs.Position = next;
        }
        return list;
    }

    /// <summary>EBML element id (marker bits kept), 0 on EOF / garbage.</summary>
    private static uint ReadId(FileStream fs)
    {
        int b = fs.ReadByte();
        if (b <= 0) return 0;
        int len = b >= 0x80 ? 1 : b >= 0x40 ? 2 : b >= 0x20 ? 3 : b >= 0x10 ? 4 : 0;
        if (len == 0) return 0;
        uint id = (uint)b;
        for (int i = 1; i < len; i++) { int n = fs.ReadByte(); if (n < 0) return 0; id = (id << 8) | (uint)n; }
        return id;
    }

    /// <summary>EBML data size (marker removed); all-ones = unknown size.</summary>
    private static long ReadSize(FileStream fs, out bool unknown)
    {
        unknown = false;
        int b = fs.ReadByte();
        if (b <= 0) { unknown = true; return 0; }
        int len = 1; int mask = 0x80;
        while (len <= 8 && (b & mask) == 0) { len++; mask >>= 1; }
        if (len > 8) { unknown = true; return 0; }
        long value = b & (mask - 1);
        bool allOnes = value == mask - 1;
        for (int i = 1; i < len; i++)
        {
            int n = fs.ReadByte(); if (n < 0) { unknown = true; return 0; }
            value = (value << 8) | (uint)n;
            if (n != 0xFF) allOnes = false;
        }
        if (allOnes) unknown = true;
        return value;
    }

    private static long ReadUInt(FileStream fs, long size)
    {
        long v = 0;
        for (long i = 0; i < size && i < 8; i++) v = (v << 8) | (uint)fs.ReadByte();
        if (size > 8) fs.Position += size - 8;
        return v;
    }
}

/// <summary>
/// Key frames from an MP4 / MOV sync-sample table (stss) of the video track:
/// decode times from stts, composition offsets from ctts, minus the edit
/// list's media_time — the presentation times ffmpeg reports for them. null
/// for a fragmented MP4 (samples in moof, not in the moov tables) or a track
/// without stss (all samples are sync then — not worth an index).
/// </summary>
internal static class Mp4SyncSamples
{
    public static KeyframeIndex? Read(string path, int videoStreamIndex)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        var moov = FindBox(fs, 0, fs.Length, "moov");
        if (moov is null) return null;
        var (moovStart, moovEnd) = moov.Value;
        if (moovEnd - moovStart > 256L * 1024 * 1024) return null;
        var buf = new byte[moovEnd - moovStart];
        fs.Position = moovStart;
        fs.ReadExactly(buf);

        int ordinal = 0;
        foreach (var (trakStart, trakEnd) in Children(buf, 0, buf.Length, "trak"))
        {
            var mdia = Child(buf, trakStart, trakEnd, "mdia");
            if (mdia is null) { ordinal++; continue; }
            var hdlr = Child(buf, mdia.Value.Start, mdia.Value.End, "hdlr");
            bool video = hdlr is not null && Encoding.ASCII.GetString(buf, hdlr.Value.Start + 8, 4) == "vide";
            int thisOrdinal = ordinal++;
            if (!video) continue;
            if (videoStreamIndex >= 0 && thisOrdinal != videoStreamIndex) continue;

            var mdhd = Child(buf, mdia.Value.Start, mdia.Value.End, "mdhd");
            if (mdhd is null) return null;
            int v = buf[mdhd.Value.Start];
            long timescale = v == 1 ? BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(mdhd.Value.Start + 20))
                                    : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(mdhd.Value.Start + 12));
            if (timescale <= 0) return null;

            long mediaTime = 0;
            var edts = Child(buf, trakStart, trakEnd, "edts");
            if (edts is not null && Child(buf, edts.Value.Start, edts.Value.End, "elst") is { } elst)
            {
                int ev = buf[elst.Start];
                uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(elst.Start + 4));
                int p = elst.Start + 8;
                for (uint i = 0; i < count; i++)
                {
                    long mt = ev == 1 ? BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(p + 8))
                                      : BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(p + 4));
                    p += ev == 1 ? 20 : 12;
                    if (mt >= 0) { mediaTime = mt; break; }   // skip empty edits
                }
            }

            var minf = Child(buf, mdia.Value.Start, mdia.Value.End, "minf");
            var stbl = minf is null ? null : Child(buf, minf.Value.Start, minf.Value.End, "stbl");
            if (stbl is null) return null;
            var stts = Child(buf, stbl.Value.Start, stbl.Value.End, "stts");
            var stss = Child(buf, stbl.Value.Start, stbl.Value.End, "stss");
            var ctts = Child(buf, stbl.Value.Start, stbl.Value.End, "ctts");
            if (stts is null || stss is null) return null;

            // Sync sample numbers (1-based).
            uint syncCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(stss.Value.Start + 4));
            var sync = new HashSet<uint>();
            for (uint i = 0; i < syncCount; i++)
                sync.Add(BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(stss.Value.Start + 8 + (int)i * 4)));
            if (sync.Count == 0) return null;

            // Composition offsets per sample (run-length).
            var cttsRuns = new List<(uint Count, long Offset)>();
            if (ctts is not null)
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(ctts.Value.Start + 4));
                for (uint i = 0; i < n; i++)
                {
                    int p = ctts.Value.Start + 8 + (int)i * 8;
                    uint c = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
                    long off = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(p + 4));
                    cttsRuns.Add((c, off));
                }
            }

            var times = new List<double>(sync.Count);
            uint entries = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(stts.Value.Start + 4));
            long dts = 0; uint sample = 1;
            int cttsRun = 0; uint cttsLeft = cttsRuns.Count > 0 ? cttsRuns[0].Count : 0;
            for (uint e = 0; e < entries; e++)
            {
                int p = stts.Value.Start + 8 + (int)e * 8;
                uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
                uint delta = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
                for (uint i = 0; i < count; i++, sample++)
                {
                    long cto = 0;
                    if (cttsRuns.Count > 0)
                    {
                        while (cttsLeft == 0 && cttsRun + 1 < cttsRuns.Count) cttsLeft = cttsRuns[++cttsRun].Count;
                        cto = cttsRuns[cttsRun].Offset;
                        if (cttsLeft > 0) cttsLeft--;
                    }
                    if (sync.Contains(sample)) times.Add((double)(dts + cto - mediaTime) / timescale);
                    dts += delta;
                }
            }
            return KeyframeIndexService.Normalize(times, "stss");
        }
        return null;
    }

    private static (long Start, long End)? FindBox(FileStream fs, long start, long end, string type)
    {
        var hdr = new byte[16];
        long pos = start;
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(hdr, 0, 16) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var t = Encoding.ASCII.GetString(hdr, 4, 4);
            int headerLen = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); headerLen = 16; }
            else if (size == 0) size = end - pos;
            if (size < headerLen) return null;
            if (t == type) return (pos + headerLen, pos + size);
            pos += size;
        }
        return null;
    }

    private static IEnumerable<(int Start, int End)> Children(byte[] buf, int start, int end, string type)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
            int headerLen = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos + 8)); headerLen = 16; }
            else if (size == 0) size = end - pos;
            if (size < headerLen || pos + size > end) yield break;
            if (Encoding.ASCII.GetString(buf, pos + 4, 4) == type) yield return (pos + headerLen, (int)(pos + size));
            pos += (int)size;
        }
    }

    private static (int Start, int End)? Child(byte[] buf, int start, int end, string type)
    {
        foreach (var c in Children(buf, start, end, type)) return c;
        return null;
    }
}
