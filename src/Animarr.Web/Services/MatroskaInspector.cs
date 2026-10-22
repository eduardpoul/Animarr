namespace Animarr.Web.Services;

/// <summary>
/// Reads just enough of a Matroska file's header to tell whether a TV's
/// player (Media3) can Direct Play it well: is there a Cues index (without one
/// Media3 can't seek an MKV), and are any tracks content-compressed with
/// anything but header stripping (zlib / bzlib / LZO — Media3 rejects them)?
/// Walks the top-level elements of the Segment up to the first Cluster,
/// skipping payloads by seeking (attachments are never read), and follows the
/// SeekHead to a Cues element stored at the end of the file.
/// </summary>
public static class MatroskaInspector
{
    private const uint EbmlId        = 0x1A45DFA3;
    private const uint SegmentId     = 0x18538067;
    private const uint SeekHeadId    = 0x114D9B74;
    private const uint SeekId        = 0x4DBB;
    private const uint SeekIdId      = 0x53AB;
    private const uint SeekPosId     = 0x53AC;
    private const uint TracksId      = 0x1654AE6B;
    private const uint TrackEntryId  = 0xAE;
    private const uint EncodingsId   = 0x6D80;
    private const uint EncodingId    = 0x6240;
    private const uint CompressionId = 0x5034;
    private const uint CompAlgoId    = 0x4254;
    private const uint CuesId        = 0x1C53BB6B;
    private const uint ClusterId     = 0x1F43B675;

    /// <summary>Header stripping — the one compression Media3 handles.</summary>
    private const ulong HeaderStripping = 3;
    private const long MaxTracksBytes = 16 * 1024 * 1024;

    /// <param name="HasCues">A Cues index exists (front, or where the SeekHead points).</param>
    /// <param name="CompressedTracks">At least one track uses zlib / bzlib / LZO content compression.</param>
    public sealed record Result(bool HasCues, bool CompressedTracks);

    /// <summary>Null when the file isn't a readable Matroska / WebM file.</summary>
    public static Result? Inspect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            if (ReadId(fs) != EbmlId) return null;
            var ebmlSize = ReadSize(fs);
            if (ebmlSize is not long es) return null;
            fs.Seek(es, SeekOrigin.Current);

            if (ReadId(fs) != SegmentId) return null;
            var segSize = ReadSize(fs);
            long segStart = fs.Position;
            long segEnd = segSize is long ss ? Math.Min(fs.Length, segStart + ss) : fs.Length;

            bool hasCues = false, compressed = false;
            var seeks = new List<(uint Id, long Pos)>();
            while (fs.Position < segEnd)
            {
                var id = ReadId(fs);
                var size = ReadSize(fs);
                if (id == 0 || size is not long sz) break;
                long dataStart = fs.Position;
                if (id == ClusterId) break;
                switch (id)
                {
                    case CuesId:
                        hasCues = true;
                        break;
                    case SeekHeadId:
                        seeks.AddRange(ReadSeekHead(fs, dataStart + sz));
                        break;
                    case TracksId when sz <= MaxTracksBytes && sz <= fs.Length - dataStart:
                        var buf = new byte[sz];
                        fs.ReadExactly(buf);
                        compressed |= TracksCompressed(buf);
                        break;
                }
                fs.Seek(dataStart + sz, SeekOrigin.Begin);
            }

            // Cues at the end: trust a SeekHead entry only if an element with
            // the Cues ID really sits there; follow one level of a second
            // SeekHead (mkvmerge / ffmpeg may put one at the end).
            if (!hasCues)
            {
                foreach (var (sid, pos) in seeks.Where(s => s.Id == SeekHeadId).ToList())
                {
                    fs.Seek(segStart + pos, SeekOrigin.Begin);
                    if (ReadId(fs) == SeekHeadId && ReadSize(fs) is long hs)
                        seeks.AddRange(ReadSeekHead(fs, fs.Position + hs));
                }
                foreach (var (sid, pos) in seeks.Where(s => s.Id == CuesId))
                {
                    if (segStart + pos >= fs.Length) continue;
                    fs.Seek(segStart + pos, SeekOrigin.Begin);
                    if (ReadId(fs) == CuesId) { hasCues = true; break; }
                }
            }
            return new Result(hasCues, compressed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException
                                       or ArgumentException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static List<(uint Id, long Pos)> ReadSeekHead(Stream s, long end)
    {
        var list = new List<(uint, long)>();
        while (s.Position < end)
        {
            var id = ReadId(s);
            if (ReadSize(s) is not long size || id == 0) break;
            long next = s.Position + size;
            if (id == SeekId)
            {
                uint target = 0;
                long pos = -1;
                while (s.Position < next)
                {
                    var cid = ReadId(s);
                    if (ReadSize(s) is not long cs || cid == 0 || cs < 0) break;
                    // SeekID / SeekPosition are ≤ 8 bytes; anything bigger (a
                    // garbage size from a damaged file) is skipped, never
                    // allocated.
                    if (cs > 8 || cs > s.Length - s.Position) { s.Seek(Math.Min(s.Length, s.Position + cs), SeekOrigin.Begin); continue; }
                    var data = new byte[cs];
                    s.ReadExactly(data);
                    if (cid == SeekIdId) target = (uint)ReadUInt(data);
                    else if (cid == SeekPosId) pos = (long)ReadUInt(data);
                }
                if (target != 0 && pos >= 0) list.Add((target, pos));
            }
            s.Seek(next, SeekOrigin.Begin);
        }
        return list;
    }

    /// <summary>Any TrackEntry → ContentEncodings → ContentEncoding →
    /// ContentCompression whose ContentCompAlgo (default 0 = zlib) isn't
    /// header stripping.</summary>
    private static bool TracksCompressed(ReadOnlySpan<byte> tracks)
    {
        foreach (var (id, entry) in Children(tracks))
        {
            if (id != TrackEntryId) continue;
            foreach (var (eid, encodings) in Children(entry))
            {
                if (eid != EncodingsId) continue;
                foreach (var (cid, encoding) in Children(encodings))
                {
                    if (cid != EncodingId) continue;
                    foreach (var (pid, compression) in Children(encoding))
                    {
                        if (pid != CompressionId) continue;
                        ulong algo = 0;
                        foreach (var (aid, value) in Children(compression))
                            if (aid == CompAlgoId) algo = ReadUInt(value);
                        if (algo != HeaderStripping) return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>The child elements of a master element's payload.</summary>
    private static List<(uint Id, byte[] Data)> Children(ReadOnlySpan<byte> data)
    {
        var list = new List<(uint, byte[])>();
        int i = 0;
        while (i < data.Length)
        {
            if (!TryVint(data[i..], keepMarker: true, out var id, out var idLen)) break;
            i += idLen;
            if (i >= data.Length || !TryVint(data[i..], keepMarker: false, out var size, out var sizeLen)) break;
            i += sizeLen;
            if (size > (ulong)(data.Length - i)) break;
            list.Add(((uint)id, data.Slice(i, (int)size).ToArray()));
            i += (int)size;
        }
        return list;
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> data)
    {
        ulong v = 0;
        foreach (var b in data[..Math.Min(8, data.Length)]) v = (v << 8) | b;
        return v;
    }

    private static uint ReadId(Stream s)
    {
        int first = s.ReadByte();
        if (first <= 0) return 0;
        int len = LeadingZeros((byte)first) + 1;
        if (len > 4) return 0;
        uint v = (uint)first;
        for (int k = 1; k < len; k++)
        {
            int b = s.ReadByte();
            if (b < 0) return 0;
            v = (v << 8) | (uint)b;
        }
        return v;
    }

    /// <summary>An element size; null for the "unknown size" marker.</summary>
    private static long? ReadSize(Stream s)
    {
        int first = s.ReadByte();
        if (first <= 0) return null;
        int len = LeadingZeros((byte)first) + 1;
        if (len > 8) return null;
        ulong v = (ulong)(first & (0xFF >> len));
        bool allOnes = v == (ulong)(0xFF >> len);
        for (int k = 1; k < len; k++)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            v = (v << 8) | (uint)b;
            allOnes &= b == 0xFF;
        }
        return allOnes ? null : checked((long)v);
    }

    private static bool TryVint(ReadOnlySpan<byte> data, bool keepMarker, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.IsEmpty || data[0] == 0) return false;
        length = LeadingZeros(data[0]) + 1;
        if (length > 8 || length > data.Length) return false;
        value = keepMarker ? data[0] : (ulong)(data[0] & (0xFF >> length));
        for (int k = 1; k < length; k++) value = (value << 8) | data[k];
        return true;
    }

    private static int LeadingZeros(byte b)
    {
        int n = 0;
        for (int mask = 0x80; mask != 0 && (b & mask) == 0; mask >>= 1) n++;
        return n;
    }
}
