using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

/// <summary>
/// The font attachments of a Matroska file — what an ASS track's styles are
/// typeset with (fansubs ship their fonts inside the MKV). A libass renderer
/// (JASSUB on the web) needs them as separate files, so they are dumped once
/// per file version into <c>&lt;cacheRoot&gt;/subtitles/fonts/&lt;key&gt;/</c>
/// (<c>ffmpeg -dump_attachment</c>: attachments sit in the container header,
/// no full read) together with a manifest of each font's family / full /
/// PostScript name read from its OpenType <c>name</c> table — the names
/// libass asks for. The fonts directory is size-capped, oldest first; one
/// attachment over <see cref="MaxFontBytes"/> isn't a font anyone ships (and
/// would sit in the cache whole), so it is skipped, as is everything past
/// <see cref="MaxSetBytes"/> for one file.
/// </summary>
public sealed class SubtitleFontService
{
    private const long MaxCacheBytes = 512L * 1024 * 1024;
    /// <summary>Largest attachment taken as a font (big CJK fonts are ~20–30 MB).</summary>
    internal const long MaxFontBytes = 50L * 1024 * 1024;
    /// <summary>Most font bytes kept for one file — the set being extracted is
    /// exempt from the cache sweep, so it needs its own cap.</summary>
    internal const long MaxSetBytes  = 256L * 1024 * 1024;
    /// <summary>Most bytes read from a font to find its names (the name table).</summary>
    private const int MaxNameTableBytes = 1024 * 1024;
    private static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(60);

    private readonly MediaProbeCache _probeCache;
    private readonly string _root;
    private readonly ILogger<SubtitleFontService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Font>>>> _extractions = new();

    public SubtitleFontService(MediaProbeCache probeCache, MediaCachePaths paths, ILogger<SubtitleFontService> logger)
    {
        _probeCache = probeCache;
        _root       = Path.Combine(paths.CacheRoot, "subtitles", "fonts");
        _logger     = logger;
    }

    /// <summary>One font attachment.</summary>
    /// <param name="Id">The attachment's absolute stream index — what
    /// <c>/api/subtitle/font?id=</c> takes.</param>
    /// <param name="Family">Typographic family (name ID 16, else 1), e.g. "Arial".</param>
    /// <param name="FullName">Full name (name ID 4), e.g. "Arial Bold".</param>
    /// <param name="PostScriptName">PostScript name (name ID 6), e.g. "Arial-BoldMT".</param>
    public sealed record Font(int Id, string FileName, string? MimeType, long? SizeBytes,
        string? Family, string? FullName, string? PostScriptName);

    /// <summary>The fonts attached to <paramref name="fullPath"/> (already
    /// validated), names included — extracting them on the first call for
    /// this file version. Empty when the file has none.</summary>
    public async Task<IReadOnlyList<Font>> ListAsync(string fullPath, CancellationToken ct)
    {
        var attachments = await AttachmentsAsync(fullPath, ct);
        if (attachments.Count == 0) return [];
        var dir = DirFor(fullPath);
        if (dir is null) return attachments;

        if (_extractions.Count > 1024) _extractions.Clear();   // memo only — the manifest on disk stays
        var lazy = _extractions.GetOrAdd(dir, d => new Lazy<Task<IReadOnlyList<Font>>>(
            () => ExtractAsync(fullPath, d, attachments)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _extractions.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<Font>>>>(dir, lazy));
            _logger.LogWarning(ex, "Font attachments of {Path} could not be extracted", fullPath);
            return attachments;
        }
    }

    /// <summary>The extracted file of font <paramref name="id"/> and its
    /// content type; null when the file has no such font attachment.</summary>
    public async Task<(string Path, string ContentType)?> GetFontFileAsync(string fullPath, int id, CancellationToken ct)
    {
        var fonts = await ListAsync(fullPath, ct);
        var font = fonts.FirstOrDefault(f => f.Id == id);
        var dir = DirFor(fullPath);
        if (font is null || dir is null) return null;
        var file = Path.Combine(dir, FontFileName(font));
        if (!File.Exists(file)) return null;
        return (file, ContentTypeOf(font));
    }

    // ─── Attachments from the cached probe ────────────────────────────────────

    private async Task<IReadOnlyList<Font>> AttachmentsAsync(string fullPath, CancellationToken ct)
    {
        var json = await _probeCache.GetStreamsJsonAsync(fullPath, ct);
        if (json is null) return [];
        var list = new List<Font>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "attachment") continue;
            if (!s.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out var index)) continue;
            string? fileName = null, mime = null;
            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Value.ValueKind != JsonValueKind.String) continue;
                    if (tag.Name.Equals("filename", StringComparison.OrdinalIgnoreCase)) fileName = tag.Value.GetString();
                    else if (tag.Name.Equals("mimetype", StringComparison.OrdinalIgnoreCase)) mime = tag.Value.GetString();
                }
            var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
            if (!IsFont(fileName, mime, codec)) continue;
            long? size = s.TryGetProperty("extradata_size", out var es) && es.TryGetInt64(out var n) && n > 0 ? n : null;
            if (size > MaxFontBytes)
            {
                _logger.LogInformation("Skipping attachment {Index} of {Path}: {Bytes} bytes is too big for a font",
                    index, fullPath, size);
                continue;
            }
            list.Add(new Font(index, fileName ?? $"font{index}", mime, size, null, null, null));
        }
        return list;
    }

    private static bool IsFont(string? fileName, string? mime, string? codec)
    {
        if (codec is "ttf" or "otf") return true;
        if (mime is not null && (mime.Contains("font", StringComparison.OrdinalIgnoreCase)
                                 || mime.Contains("truetype", StringComparison.OrdinalIgnoreCase)
                                 || mime.Contains("opentype", StringComparison.OrdinalIgnoreCase)))
            return true;
        return Path.GetExtension(fileName ?? "").ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc" or ".woff" or ".woff2";
    }

    // ─── Extraction ───────────────────────────────────────────────────────────

    /// <summary>
    /// The directory the fonts attached to <paramref name="fullPath"/> are
    /// extracted into (this file version) — for libass outside the browser,
    /// e.g. the HLS subtitle burn-in's <c>fontsdir</c>. Extracts them first;
    /// null when the file has no font attachments (or they couldn't be
    /// extracted).
    /// </summary>
    public async Task<string?> FontDirectoryAsync(string fullPath, CancellationToken ct)
    {
        var fonts = await ListAsync(fullPath, ct);
        var dir = DirFor(fullPath);
        return fonts.Count > 0 && dir is not null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Cache directory of this file version; null when the file is gone.</summary>
    private string? DirFor(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            // fonts2: sets extracted before the size caps are redone.
            var key = $"{fullPath}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|fonts2";
            return Path.Combine(_root, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key))));
        }
        catch { return null; }
    }

    private async Task<IReadOnlyList<Font>> ExtractAsync(string fullPath, string dir, IReadOnlyList<Font> attachments)
    {
        var manifest = Path.Combine(dir, "manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                var known = JsonSerializer.Deserialize<List<Font>>(await File.ReadAllTextAsync(manifest));
                if (known is not null && known.Count == attachments.Count) return known;
            }
            catch (JsonException) { /* rebuild below */ }
        }

        Directory.CreateDirectory(_root);
        SweepIfLarge(dir);
        var work = dir + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(work);
        try
        {
            // -dump_attachment writes while the input is opened; the null
            // output with -t 0 just gives ffmpeg something to finish.
            var psi = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                RedirectStandardOutput = false,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }) psi.ArgumentList.Add(a);
            // What the probe sized stops at the set cap; the sizes it didn't
            // report are checked after the dump.
            long planned = 0;
            var wanted = new List<Font>(attachments.Count);
            foreach (var f in attachments)
            {
                if (f.SizeBytes is long known && (planned += known) > MaxSetBytes) break;
                wanted.Add(f);
            }
            foreach (var f in wanted)
            {
                psi.ArgumentList.Add($"-dump_attachment:{f.Id.ToString(CultureInfo.InvariantCulture)}");
                psi.ArgumentList.Add(Path.Combine(work, FontFileName(f)));
            }
            foreach (var a in new[] { "-i", fullPath, "-t", "0", "-f", "null", "-" }) psi.ArgumentList.Add(a);

            using (var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start"))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(DumpTimeout);
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    throw new TimeoutException("ffmpeg -dump_attachment timed out");
                }
                var err = await stderr;
                if (p.ExitCode != 0)
                    _logger.LogDebug("ffmpeg -dump_attachment exit {Code} for {Path}: {Err}", p.ExitCode, fullPath, err.Trim());
            }

            var fonts = new List<Font>(wanted.Count);
            long kept = 0;
            foreach (var f in wanted)
            {
                var file = Path.Combine(work, FontFileName(f));
                long length = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (length == 0) continue;
                if (length > MaxFontBytes || kept + length > MaxSetBytes)
                {
                    _logger.LogInformation("Dropping attachment {Id} of {Path}: {Bytes} bytes (font / set cap)", f.Id, fullPath, length);
                    try { File.Delete(file); } catch { }
                    continue;
                }
                kept += length;
                var names = ReadNames(file);
                fonts.Add(f with
                {
                    SizeBytes = length,
                    Family = names.Family, FullName = names.FullName, PostScriptName = names.PostScript,
                });
            }
            await File.WriteAllTextAsync(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(fonts));
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(work, dir);
            }
            catch (IOException) when (File.Exists(manifest))
            {
                // A concurrent extraction (another process) got there first.
            }
            return fonts;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>Drop the oldest font sets while the directory is over its cap.</summary>
    private void SweepIfLarge(string keep)
    {
        try
        {
            var sets = new DirectoryInfo(_root).EnumerateDirectories()
                .Select(d => (Dir: d, Bytes: d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)))
                .ToList();
            var total = sets.Sum(s => s.Bytes);
            foreach (var s in sets.OrderBy(s => s.Dir.LastWriteTimeUtc))
            {
                if (total <= MaxCacheBytes) break;
                if (string.Equals(s.Dir.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { s.Dir.Delete(true); total -= s.Bytes; } catch { }
                _extractions.TryRemove(s.Dir.FullName, out _);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>On-disk name: the id plus a sanitized extension (never the
    /// attachment's own file name — it comes from the file).</summary>
    private static string FontFileName(Font f)
    {
        var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
        if (ext.Length is < 2 or > 6 || !ext[1..].All(char.IsAsciiLetterOrDigit)) ext = ".bin";
        return f.Id.ToString(CultureInfo.InvariantCulture) + ext;
    }

    private static string ContentTypeOf(Font f)
        => Path.GetExtension(f.FileName).ToLowerInvariant() switch
        {
            ".ttf"            => "font/ttf",
            ".otf"            => "font/otf",
            ".ttc" or ".otc"  => "font/collection",
            ".woff"           => "font/woff",
            ".woff2"          => "font/woff2",
            _                 => "application/octet-stream",
        };

    // ─── OpenType name table ──────────────────────────────────────────────────

    /// <summary>Family / full / PostScript name of the TrueType / OpenType font
    /// in <paramref name="file"/> (the first font of a collection), reading only
    /// the table directory and the <c>name</c> table — never the whole font.
    /// Nulls for WOFF or anything unreadable.</summary>
    internal static (string? Family, string? FullName, string? PostScript) ReadNames(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            var head = new byte[16];
            if (ReadAt(fs, 0, head) < 12) return default;
            long sfnt = head.AsSpan(0, 4).SequenceEqual("ttcf"u8) ? BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12)) : 0;
            var offsets = new byte[12];
            if (ReadAt(fs, sfnt, offsets) < 12) return default;
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(offsets.AsSpan(4));
            var records = new byte[numTables * 16];
            if (ReadAt(fs, sfnt + 12, records) < records.Length) return default;
            for (int i = 0; i < numTables; i++)
            {
                var rec = records.AsSpan(i * 16, 16);
                if (!rec[..4].SequenceEqual("name"u8)) continue;
                long offset = BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
                long length = BinaryPrimitives.ReadUInt32BigEndian(rec[12..]);
                if (length < 6 || length > MaxNameTableBytes) return default;
                var table = new byte[length];
                return ReadAt(fs, offset, table) == table.Length ? ReadNameTable(table) : default;
            }
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>;
    /// the bytes read (fewer at the end of the file).</summary>
    private static int ReadAt(FileStream fs, long offset, byte[] buffer)
    {
        if (offset < 0 || offset >= fs.Length) return 0;
        fs.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = fs.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>The wanted names out of an OpenType <c>name</c> table.</summary>
    private static (string? Family, string? FullName, string? PostScript) ReadNameTable(ReadOnlySpan<byte> table)
    {
        try
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            int strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);

            // Best record per name ID: Windows US English > Windows any > Unicode > Mac.
            var best = new Dictionary<int, (int Rank, string Text)>();
            for (int i = 0; i < count; i++)
            {
                int r = 6 + i * 12;
                if (table.Length < r + 12) break;
                int platform = BinaryPrimitives.ReadUInt16BigEndian(table[r..]);
                int encoding = BinaryPrimitives.ReadUInt16BigEndian(table[(r + 2)..]);
                int language = BinaryPrimitives.ReadUInt16BigEndian(table[(r + 4)..]);
                int nameId   = BinaryPrimitives.ReadUInt16BigEndian(table[(r + 6)..]);
                int length   = BinaryPrimitives.ReadUInt16BigEndian(table[(r + 8)..]);
                int offset   = BinaryPrimitives.ReadUInt16BigEndian(table[(r + 10)..]);
                if (nameId is not (1 or 4 or 6 or 16)) continue;
                int start = strings + offset;
                if (table.Length < start + length) continue;
                var bytes = table.Slice(start, length);
                int rank;
                string text;
                if (platform == 3 && encoding is 0 or 1 or 10)
                {
                    rank = language == 0x0409 ? 0 : 1;
                    text = Encoding.BigEndianUnicode.GetString(bytes);
                }
                else if (platform == 0)
                {
                    rank = 2;
                    text = Encoding.BigEndianUnicode.GetString(bytes);
                }
                else if (platform == 1 && encoding == 0)
                {
                    rank = 3;
                    text = Encoding.Latin1.GetString(bytes);
                }
                else continue;
                text = text.Trim('\0', ' ');
                if (text.Length == 0) continue;
                if (!best.TryGetValue(nameId, out var have) || rank < have.Rank) best[nameId] = (rank, text);
            }
            string? Get(int id) => best.TryGetValue(id, out var v) ? v.Text : null;
            return (Get(16) ?? Get(1), Get(4), Get(6));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException)
        {
            return default;
        }
    }
}
