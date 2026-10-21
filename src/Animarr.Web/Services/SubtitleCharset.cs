using System.Collections.Concurrent;
using System.Text;

namespace Animarr.Web.Services;

/// <summary>
/// Character-set detection (and conversion to UTF-8) for TEXT subtitles —
/// SRT / ASS / VTT …
///
/// ffmpeg's text-subtitle decoders insist on UTF-8: a Russian SRT saved in
/// Windows-1251 (still the norm for subs from Russian trackers) failed every
/// cue with "Invalid UTF-8 in decoded subtitles text", so /api/subtitle answered
/// 422 and the track stayed empty. A partly-broken file was worse: ffmpeg
/// tolerates up to 2/3 bad cues and exits 0 with those cues missing.
///
/// Why the bytes are re-encoded here instead of passing <c>-sub_charenc</c>:
/// ffmpeg's option only recodes subtitle PACKETS. The ASS header (the
/// <c>[V4+ Styles]</c> block, carried as extradata) stays in the old code page,
/// so a style called "Надписи" no longer matches the now-UTF-8 dialogue lines
/// that reference it (verified with ffmpeg N-124716). Converting the whole file
/// to UTF-8 first keeps header and events consistent, and also covers BOM-less
/// UTF-16, which ffmpeg's demuxers can't even split into lines.
///
/// Decision order (<see cref="Detect"/>):
///   1. BOM — UTF-8 or UTF-16 LE/BE. ffmpeg's text reader decodes these itself
///      (verified), so: no conversion.
///   2. Valid UTF-8 (pure ASCII included) → as is. "Mostly UTF-8 with a few
///      stray bytes" counts as UTF-8 too: re-reading it as a code page would
///      garble every correct line to rescue a few broken ones.
///   3. NUL bytes: the alternating-zero pattern of BOM-less UTF-16 → convert;
///      any other binary-looking data (a VobSub .sub) → leave alone.
///   4. A single-byte code page. If the 0xC0–0xFF letters mostly stand next to
///      each other (whole words of them) rather than between ASCII letters,
///      the text is Cyrillic — a Western accented letter sits inside an ASCII
///      word. Windows-1251 vs KOI8-R is then decided by Russian letter
///      frequencies: each candidate maps the bytes to letters, and only the
///      right one yields the frequent ones (о е а и н т с …). Otherwise
///      Windows-1252 (Western European).
///
/// Out of scope (conservative): the other Windows code pages (1250 Central
/// European, 1253 Greek, …), CP866, UTF-32.
/// </summary>
public static class SubtitleCharset
{
    /// <param name="Name">What was detected, for logs.</param>
    /// <param name="CodePage">The encoding to convert FROM before ffmpeg sees
    /// the text; null = hand the bytes to ffmpeg as they are.</param>
    public sealed record Detection(string Name, int? CodePage)
    {
        public bool NeedsConversion => CodePage is not null;
    }

    public static readonly Detection Utf8        = new("UTF-8", null);
    public static readonly Detection Utf8Bom     = new("UTF-8 (BOM)", null);
    public static readonly Detection Utf8Damaged = new("UTF-8 (a few invalid bytes)", null);
    public static readonly Detection Utf16LeBom  = new("UTF-16LE (BOM)", null);
    public static readonly Detection Utf16BeBom  = new("UTF-16BE (BOM)", null);
    public static readonly Detection Binary      = new("binary / unknown", null);
    public static readonly Detection Utf16Le     = new("UTF-16LE (no BOM)", 1200);
    public static readonly Detection Utf16Be     = new("UTF-16BE (no BOM)", 1201);
    public static readonly Detection Windows1251 = new("Windows-1251 (Cyrillic)", 1251);
    public static readonly Detection Koi8R       = new("KOI8-R (Cyrillic)", 20866);
    public static readonly Detection Windows1252 = new("Windows-1252 (Western)", 1252);

    /// <summary>Subtitle files are judged by at most this many leading bytes —
    /// plenty for a statistical decision.</summary>
    public const int MaxSampleBytes = 4 * 1024 * 1024;

    /// <summary>Larger "subtitle" files are not converted (no real text
    /// subtitle gets near this; it bounds the memory of a conversion).</summary>
    public const int MaxConvertBytes = 32 * 1024 * 1024;

    /// <summary>Extensions of standalone text subtitle files (as opposed to a
    /// container holding a subtitle stream).</summary>
    public static bool IsTextSubtitleFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt"
            or ".sub" or ".txt" or ".smi" or ".sami";

    // ─── Detection ──────────────────────────────────────────────────────────

    /// <param name="data">The file's bytes (or its first <see cref="MaxSampleBytes"/>).</param>
    /// <param name="truncated">True when <paramref name="data"/> is only a prefix:
    /// a multi-byte UTF-8 sequence cut at the end is then not an error.</param>
    public static Detection Detect(ReadOnlySpan<byte> data, bool truncated = false)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Utf8Bom;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Utf16LeBom;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Utf16BeBom;

        var (validMultiByte, invalid, nulEven, nulOdd) = ScanUtf8(data, truncated);
        int nul = nulEven + nulOdd;
        if (nul > 0 && nul * 100 >= data.Length)   // ≥1 % NUL bytes: not 8-bit text
        {
            // BOM-less UTF-16: every ASCII code unit (digits, timestamps,
            // spaces, punctuation — a large share of any subtitle file, even a
            // Cyrillic one, whose letters are 0x04xx) has a zero high byte, so
            // the NULs sit almost only on one parity — odd offsets for
            // little-endian, even for big-endian. Binary formats scatter them.
            if (nul * 10 >= data.Length && nulOdd >= nul * 9 / 10) return Utf16Le;
            if (nul * 10 >= data.Length && nulEven >= nul * 9 / 10) return Utf16Be;
            return Binary;
        }
        if (invalid == 0) return Utf8;
        if (validMultiByte > 0 && validMultiByte >= invalid * 8) return Utf8Damaged;

        // Single-byte code page. The 0xC0–0xFF block is letters in all three
        // candidates (Cyrillic in 1251/KOI8-R, accented Latin in 1252); what
        // tells them apart is how those bytes sit in words. A Cyrillic word is
        // high bytes back to back ("Привет"); a Western accented letter sits
        // between ASCII letters ("très", "Größe"). Counting neighbours rather
        // than the share of letters keeps an ASS file honest, whose [V4+ Styles]
        // header and "Dialogue: 0,…,Default,," prefixes are all ASCII.
        int ascii = 0, high = 0, highNeighbour = 0, asciiNeighbour = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (IsAsciiLetter(b)) { ascii++; continue; }
            if (b < 0xC0) continue;
            high++;
            byte prev = i > 0 ? data[i - 1] : (byte)0;
            byte next = i + 1 < data.Length ? data[i + 1] : (byte)0;
            if (prev >= 0xC0 || next >= 0xC0) highNeighbour++;
            if (IsAsciiLetter(prev) || IsAsciiLetter(next)) asciiNeighbour++;
        }
        bool cyrillic = highNeighbour != asciiNeighbour
            ? highNeighbour > asciiNeighbour
            : high * 100 >= (high + ascii) * 35;   // only isolated letters: fall back to their share
        if (!cyrillic) return Windows1252;

        double score1251 = 0, scoreKoi = 0;
        foreach (var b in data)
        {
            if (b < 0xC0) continue;
            int pos = b & 0x1F;                              // 0..31 within either case half
            score1251 += RussianFrequency[pos];              // 1251: А..Я / а..я in alphabet order
            scoreKoi  += RussianFrequency[Koi8Order[pos]];   // KOI8-R: its own order, both halves
        }
        return scoreKoi > score1251 ? Koi8R : Windows1251;
    }

    /// <summary>Strict UTF-8 walk: counts well-formed multi-byte sequences,
    /// bytes that can't be UTF-8, and NUL bytes by offset parity.</summary>
    private static (int ValidMultiByte, int Invalid, int NulEven, int NulOdd) ScanUtf8(
        ReadOnlySpan<byte> data, bool truncated)
    {
        int valid = 0, invalid = 0, nulEven = 0, nulOdd = 0, i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b < 0x80)
            {
                if (b == 0) { if ((i & 1) == 0) nulEven++; else nulOdd++; }
                i++;
                continue;
            }
            int need = b switch
            {
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _                   => -1,   // continuation byte without a lead, C0/C1, F5+
            };
            if (need < 0) { invalid++; i++; continue; }
            if (i + need >= data.Length)
            {
                // The sequence runs past the end of the data: fine when the
                // sample boundary cut it, broken when the file really ends here.
                if (truncated && AllContinuation(data[(i + 1)..])) break;
                invalid++; i++; continue;
            }
            bool ok = true;
            for (int k = 1; ok && k <= need; k++)
                ok = (data[i + k] & 0xC0) == 0x80;
            // Overlongs, surrogates and > U+10FFFF show on the second byte.
            if (ok && need >= 2)
            {
                byte b1 = data[i + 1];
                ok = b switch
                {
                    0xE0 => b1 >= 0xA0,
                    0xED => b1 <= 0x9F,
                    0xF0 => b1 >= 0x90,
                    0xF4 => b1 <= 0x8F,
                    _    => true,
                };
            }
            if (ok) { valid++; i += need + 1; }
            else    { invalid++; i++; }
        }
        return (valid, invalid, nulEven, nulOdd);
    }

    private static bool IsAsciiLetter(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool AllContinuation(ReadOnlySpan<byte> rest)
    {
        foreach (var b in rest)
            if ((b & 0xC0) != 0x80) return false;
        return true;
    }

    /// <summary>Russian letter frequencies (%), alphabet order а…я (no ё).</summary>
    private static readonly double[] RussianFrequency =
    [
        8.01, 1.59, 4.54, 1.70, 2.98, 8.45, 0.94, 1.65,   // а б в г д е ж з
        7.35, 1.21, 3.49, 4.40, 3.21, 6.70, 10.97, 2.81,  // и й к л м н о п
        4.73, 5.47, 6.26, 2.62, 0.26, 0.97, 0.48, 1.44,   // р с т у ф х ц ч
        0.73, 0.36, 0.04, 1.90, 1.74, 0.32, 0.64, 2.01,   // ш щ ъ ы ь э ю я
    ];

    /// <summary>KOI8-R byte (low 5 bits, either case half) → alphabet index.
    /// KOI8-R orders the letters "юабцдефгхийклмнопярстужвьызшэщчъ".</summary>
    private static readonly int[] Koi8Order = BuildKoi8Order();

    private static int[] BuildKoi8Order()
    {
        const string alphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        const string koi8     = "юабцдефгхийклмнопярстужвьызшэщчъ";
        var map = new int[32];
        for (int i = 0; i < 32; i++) map[i] = alphabet.IndexOf(koi8[i]);
        return map;
    }

    // ─── Conversion ─────────────────────────────────────────────────────────

    /// <summary>Re-encode <paramref name="data"/> from the detected encoding to
    /// UTF-8 without a BOM. Only valid when <see cref="Detection.NeedsConversion"/>.</summary>
    public static byte[] ToUtf8(ReadOnlySpan<byte> data, Detection detection)
    {
        var source = detection.CodePage switch
        {
            1200 => Encoding.Unicode,
            1201 => Encoding.BigEndianUnicode,
            int cp => CodePagesEncodingProvider.Instance.GetEncoding(cp)
                      ?? throw new NotSupportedException($"Code page {cp} is not available."),
            null => throw new InvalidOperationException("Nothing to convert."),
        };
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(source.GetString(data));
    }

    // ─── Per-file cache ─────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, Detection> Cache = new();
    private const int MaxCacheEntries = 4096;

    /// <summary>Cache key: source path + stream (-1 = the file itself) + size +
    /// mtime, so a replaced or re-downloaded file is a new key.</summary>
    public static string CacheKey(string path, int track = -1)
    {
        var fi = new FileInfo(path);
        return $"{path}|{track}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    public static bool TryGetCached(string key, out Detection detection)
        => Cache.TryGetValue(key, out detection!);

    public static void Store(string key, Detection detection)
    {
        if (Cache.Count >= MaxCacheEntries) Cache.Clear();   // tiny records; a rare full reset is fine
        Cache[key] = detection;
    }

    /// <summary>Detect a file's encoding from its first
    /// <see cref="MaxSampleBytes"/> (cached per path + size + mtime, or per
    /// <paramref name="cacheKey"/> when given).</summary>
    public static async Task<Detection> DetectFileAsync(string path, CancellationToken ct, string? cacheKey = null)
    {
        var key = cacheKey ?? CacheKey(path);
        if (TryGetCached(key, out var cached)) return cached;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, useAsync: true);
        var len = (int)Math.Min(fs.Length, MaxSampleBytes);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await fs.ReadAsync(buf.AsMemory(read, len - read), ct);
            if (n == 0) break;
            read += n;
        }
        var detection = Detect(buf.AsSpan(0, read), truncated: fs.Length > read);
        Store(key, detection);
        return detection;
    }
}
