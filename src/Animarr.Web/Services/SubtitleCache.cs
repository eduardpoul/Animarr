using System.Diagnostics;

namespace Animarr.Web.Services;

/// <summary>
/// Disk cache behind <c>/api/subtitle</c>. Extracting one embedded track makes
/// ffmpeg read the WHOLE container — Matroska has no index for subtitle
/// packets — so every request was a 0.3–2 GB read (seconds on an HDD), fired at
/// each playback start with subs while the video stream reads the same file.
///
/// The converted track is cached under <c>&lt;cacheRoot&gt;/subtitles</c>, keyed
/// by source path + track + format + mtime + size (like ImageResizer's
/// variants), so a re-watch, another profile or a request after the browser's
/// 1-hour TTL is a plain file send, and a replaced file busts the key. Only a
/// successful, non-empty conversion is cached — written to a temp file and
/// atomically renamed — so a bitmap track or a broken stream stays a 422 and
/// never becomes a cached empty subtitle. The directory is size-capped, oldest
/// first.
///
/// Text that isn't UTF-8 (a Windows-1251 SRT, say) is re-encoded to UTF-8
/// before ffmpeg reads it — see <see cref="SubtitleCharset"/> for the detection
/// and for why that beats <c>-sub_charenc</c>:
///   • a standalone subtitle file is detected up front (cached per file);
///   • an embedded track is converted as before, and only when ffmpeg reports
///     invalid UTF-8 are its raw packets copied out (<c>-c:s copy</c>),
///     detected, re-encoded and converted from that copy. The detection is
///     cached per (file, track), so the other output format of the same track
///     goes straight to the copy path.
/// </summary>
public static class SubtitleCache
{
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const long TrimToBytes   = 192L * 1024 * 1024;
    private const int  SweepEveryWrites = 32;
    private static int _writesSinceSweep;
    private static int _sweeping;

    /// <summary><paramref name="Path"/> = the cached track; null = conversion
    /// failed (<paramref name="ExitCode"/> / <paramref name="Error"/> = ffmpeg's).
    /// <paramref name="Charset"/> = the source encoding when it had to be
    /// converted to UTF-8 first (null = used as is / served from cache).</summary>
    public sealed record Result(string? Path, int ExitCode, string Error, string? Charset = null);

    /// <param name="outFormat">ffmpeg muxer: <c>webvtt</c> or <c>ass</c>.</param>
    /// <param name="codecLookup">ffprobe <c>codec_name</c> of subtitle stream
    /// <paramref name="track"/>. Only asked on the non-UTF-8 fallback for an
    /// embedded track, to pick the muxer its raw packets are copied out with.</param>
    public static async Task<Result> GetOrExtractAsync(string sourcePath, int track, string outFormat,
        string cacheRoot, CancellationToken ct, Func<CancellationToken, Task<string?>>? codecLookup = null)
    {
        var dir = Path.Combine(cacheRoot, "subtitles");
        Directory.CreateDirectory(dir);

        var fi     = new FileInfo(sourcePath);
        var cached = CachedPath(dir, sourcePath, track, outFormat, fi);

        if (File.Exists(cached))
        {
            TouchForLru(cached);
            return new Result(cached, 0, "");
        }

        // An ASS / SSA source asked for as ASS is served as the original
        // script (styles, typesetting, [Fonts] intact) instead of a round trip
        // through ffmpeg's ASS decoder + encoder.
        if (outFormat == "ass")
        {
            var original = await TryOriginalAssAsync(sourcePath, track, cached, fi, codecLookup, ct);
            if (original is not null) return original;
        }

        // Unique temp names: two concurrent requests for the same track each
        // extract (as before) and the second rename just replaces the first.
        var work = $"{cached}.{Guid.NewGuid():N}";
        var tmp  = work + ".tmp";
        var temps = new List<string> { tmp };
        try
        {
            Result r;
            if (SubtitleCharset.IsTextSubtitleFile(sourcePath))
            {
                // Standalone file: detect up front, convert a UTF-8 copy if needed.
                var detected = await SubtitleCharset.DetectFileAsync(sourcePath, ct);
                var input = sourcePath;
                if (detected.NeedsConversion && fi.Length <= SubtitleCharset.MaxConvertBytes)
                {
                    input = work + ".utf8.tmp";
                    temps.Add(input);
                    await File.WriteAllBytesAsync(input,
                        SubtitleCharset.ToUtf8(await File.ReadAllBytesAsync(sourcePath, ct), detected), ct);
                }
                // No forced demuxer: ffmpeg recognises SRT/ASS/VTT by content,
                // exactly as it did for the original file.
                r = await RunAsync(ConvertArgs(input, null, 0, outFormat, tmp), tmp, ct);
                if (input != sourcePath) r = r with { Charset = detected.Name };
            }
            else
            {
                // Embedded track: convert directly, and take the raw-copy path
                // only when ffmpeg says the text isn't UTF-8 — or right away when
                // an earlier request already found that out for this track.
                var trackKey = SubtitleCharset.CacheKey(sourcePath, track);
                bool knownLegacy = SubtitleCharset.TryGetCached(trackKey, out var known) && known.NeedsConversion;
                var fallback = knownLegacy
                    ? await ConvertLegacyEmbeddedAsync(sourcePath, track, outFormat, work, trackKey, codecLookup, temps, ct)
                    : null;
                if (fallback is not { Path: not null })
                {
                    r = await RunAsync(ConvertArgs(sourcePath, null, track, outFormat, tmp), tmp, ct);
                    if (!knownLegacy && r.Error.Contains("Invalid UTF-8", StringComparison.OrdinalIgnoreCase))
                        fallback = await ConvertLegacyEmbeddedAsync(sourcePath, track, outFormat, work, trackKey,
                            codecLookup, temps, ct);
                }
                else r = fallback;
                // The direct result (possibly partial — ffmpeg tolerates some bad
                // cues) stands unless the fallback produced something.
                if (fallback is { Path: { } converted })
                {
                    File.Move(converted, tmp, overwrite: true);
                    r = fallback with { Path = tmp };
                }
            }

            if (r.Path is null) return r;
            File.Move(tmp, cached, overwrite: true);

            if (Interlocked.Increment(ref _writesSinceSweep) >= SweepEveryWrites)
            {
                Interlocked.Exchange(ref _writesSinceSweep, 0);
                _ = Task.Run(() => Sweep(dir));
            }
            return r with { Path = cached };
        }
        finally
        {
            foreach (var t in temps) TryDelete(t);
        }
    }

    /// <summary>Where track <paramref name="track"/> of <paramref name="sourcePath"/>
    /// in <paramref name="outFormat"/> is cached, keyed by the file version.</summary>
    private static string CachedPath(string dir, string sourcePath, int track, string outFormat, FileInfo fi)
    {
        // "|cs1": conversions made before charset handling may hold a track
        // whose non-UTF-8 cues ffmpeg silently dropped (it exits 0 below 2/3 bad
        // cues). A new key re-converts every track once. "|orig1" (ASS only):
        // ASS / SSA sources are now copied, not re-encoded.
        var key  = $"{sourcePath}|{track}|{outFormat}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|cs1"
                   + (outFormat == "ass" ? "|orig1" : "");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(dir, $"{hash}.{(outFormat == "ass" ? "ass" : "vtt")}");
    }

    /// <summary>The cached conversion of a track, or null when it was never
    /// made (no ffmpeg run, no side effect).</summary>
    public static string? TryGetCached(string sourcePath, int track, string outFormat, string cacheRoot)
    {
        try
        {
            var path = CachedPath(Path.Combine(cacheRoot, "subtitles"), sourcePath, track, outFormat, new FileInfo(sourcePath));
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The original script of an ASS / SSA source into <paramref name="cached"/>:
    /// a standalone <c>.ass</c> / <c>.ssa</c> file as is (re-encoded to UTF-8
    /// when it isn't), an embedded ASS / SSA track as its raw packets
    /// (<c>-c:s copy -f ass</c> — the muxer writes the track's own header).
    /// null = not an ASS source (or the copy failed): convert as before.
    /// </summary>
    private static async Task<Result?> TryOriginalAssAsync(string sourcePath, int track, string cached, FileInfo fi,
        Func<CancellationToken, Task<string?>>? codecLookup, CancellationToken ct)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        bool standalone = ext is ".ass" or ".ssa";
        if (standalone)
        {
            if (track != 0 || fi.Length == 0 || fi.Length > SubtitleCharset.MaxConvertBytes) return null;
        }
        else if (SubtitleCharset.IsTextSubtitleFile(sourcePath)
                 || codecLookup is null || !SubtitleCodecs.IsAss(await codecLookup(ct)))
        {
            return null;
        }

        var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (standalone)
            {
                await File.WriteAllBytesAsync(tmp, await File.ReadAllBytesAsync(sourcePath, ct), ct);
            }
            else
            {
                var copy = await RunAsync(
                [
                    "-hide_banner", "-loglevel", "warning",
                    "-i", sourcePath, "-map", $"0:s:{track}", "-c:s", "copy", "-f", "ass", "-y", tmp,
                ], tmp, ct);
                if (copy.Path is null) return null;
            }
            var charset = await EnsureUtf8Async(tmp, ct);
            File.Move(tmp, cached, overwrite: true);
            return new Result(cached, 0, "", charset);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// Copy several embedded ASS / SSA tracks into the cache in ONE read of
    /// the container (each extraction is a full read — Matroska has no index
    /// for subtitle packets). Tracks already cached are skipped; a track whose
    /// copy fails is just left for <see cref="GetOrExtractAsync"/>.
    /// </summary>
    public static async Task EnsureOriginalAssAsync(string sourcePath, IReadOnlyCollection<int> tracks,
        string cacheRoot, CancellationToken ct)
    {
        var dir = Path.Combine(cacheRoot, "subtitles");
        Directory.CreateDirectory(dir);
        var fi = new FileInfo(sourcePath);
        var todo = tracks.Distinct()
            .Select(t => (Track: t, Cached: CachedPath(dir, sourcePath, t, "ass", fi)))
            .Where(x => !File.Exists(x.Cached))
            .Select(x => (x.Track, x.Cached, Tmp: $"{x.Cached}.{Guid.NewGuid():N}.tmp"))
            .ToList();
        if (todo.Count == 0) return;

        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-i", sourcePath };
        foreach (var x in todo)
            args.AddRange(["-map", $"0:s:{x.Track}", "-c:s", "copy", "-f", "ass", "-y", x.Tmp]);
        try
        {
            // Only a clean run is cached — a failed one may have cut any output short.
            if ((await RunAsync(args, todo[0].Tmp, ct)).ExitCode != 0) return;
            foreach (var x in todo)
            {
                if (!File.Exists(x.Tmp) || new FileInfo(x.Tmp).Length == 0) continue;
                await EnsureUtf8Async(x.Tmp, ct);
                File.Move(x.Tmp, x.Cached, overwrite: true);
            }
        }
        finally
        {
            foreach (var x in todo) TryDelete(x.Tmp);
        }
    }

    /// <summary>Re-encode a copied / standalone script to UTF-8 in place when
    /// it is in a legacy code page; the detected source charset, or null when
    /// it already was UTF-8.</summary>
    private static async Task<string?> EnsureUtf8Async(string file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        if (bytes.Length > SubtitleCharset.MaxConvertBytes) return null;
        var detected = SubtitleCharset.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, SubtitleCharset.MaxSampleBytes)),
            truncated: bytes.Length > SubtitleCharset.MaxSampleBytes);
        // ffmpeg reads a UTF-16 file with a BOM itself, a browser-side
        // renderer is told charset=utf-8 — so a served script is UTF-8.
        var bom = detected == SubtitleCharset.Utf16LeBom ? System.Text.Encoding.Unicode
                : detected == SubtitleCharset.Utf16BeBom ? System.Text.Encoding.BigEndianUnicode
                : null;
        if (bom is not null)
        {
            await File.WriteAllBytesAsync(file, System.Text.Encoding.UTF8.GetBytes(bom.GetString(bytes, 2, bytes.Length - 2)), ct);
            return detected.Name;
        }
        if (!detected.NeedsConversion) return null;
        await File.WriteAllBytesAsync(file, SubtitleCharset.ToUtf8(bytes, detected), ct);
        return detected.Name;
    }

    /// <summary>
    /// Embedded text track that isn't UTF-8 (rare — mkvmerge converts on mux,
    /// but an ffmpeg <c>-c copy</c> remux keeps whatever bytes the SRT had):
    /// copy its packets out untouched, detect their encoding, re-encode to
    /// UTF-8 and convert that. null = nothing better could be done.
    /// </summary>
    private static async Task<Result?> ConvertLegacyEmbeddedAsync(string sourcePath, int track, string outFormat,
        string work, string trackKey, Func<CancellationToken, Task<string?>>? codecLookup, List<string> temps,
        CancellationToken ct)
    {
        var codec = codecLookup is null ? null : await codecLookup(ct);
        var rawFormat = (codec ?? "").ToLowerInvariant() switch
        {
            "subrip" or "srt" or "text" => "srt",
            "ass" or "ssa"              => "ass",
            "webvtt"                    => "webvtt",
            _                           => null,   // mov_text is UTF-8/16 by spec; bitmap tracks aren't text
        };
        if (rawFormat is null) return null;

        var raw = work + ".raw.tmp";
        temps.Add(raw);
        var copy = await RunAsync(
        [
            "-hide_banner", "-loglevel", "warning",
            "-i", sourcePath, "-map", $"0:s:{track}", "-c:s", "copy", "-f", rawFormat, "-y", raw,
        ], raw, ct);
        if (copy.Path is null) return null;

        var bytes = await File.ReadAllBytesAsync(raw, ct);
        var detected = SubtitleCharset.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, SubtitleCharset.MaxSampleBytes)),
            truncated: bytes.Length > SubtitleCharset.MaxSampleBytes);
        SubtitleCharset.Store(trackKey, detected);
        if (!detected.NeedsConversion || bytes.Length > SubtitleCharset.MaxConvertBytes) return null;

        var utf8 = work + ".utf8.tmp";
        var output = work + ".out.tmp";
        temps.Add(utf8);
        temps.Add(output);
        await File.WriteAllBytesAsync(utf8, SubtitleCharset.ToUtf8(bytes, detected), ct);
        var r = await RunAsync(ConvertArgs(utf8, rawFormat, 0, outFormat, output), output, ct);
        return r with { Charset = detected.Name };
    }

    /// <summary>Convert subtitle stream <paramref name="track"/> of
    /// <paramref name="input"/> to <paramref name="outFormat"/>.
    /// <paramref name="inputFormat"/> forces the demuxer (our raw copy of an
    /// embedded track, whose format we chose); null = let ffmpeg probe.</summary>
    private static string[] ConvertArgs(string input, string? inputFormat, int track, string outFormat, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning" };
        if (inputFormat is not null) { a.Add("-f"); a.Add(inputFormat); }
        a.AddRange(["-i", input, "-map", $"0:s:{track}", "-f", outFormat, "-y", output]);
        return a.ToArray();
    }

    /// <summary>Run one ffmpeg; success = exit 0 and a non-empty
    /// <paramref name="output"/> (then <see cref="Result.Path"/> = output).</summary>
    private static async Task<Result> RunAsync(IEnumerable<string> args, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            // Output goes to a file, nothing to read on stdout.
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        // Drain stderr alongside the run — a full stderr pipe stalls ffmpeg.
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch
        {
            // Client went away (or worse): stop the full-file read; the caller
            // drops the partial output.
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw;
        }
        var err = await stderr;

        var length = File.Exists(output) ? new FileInfo(output).Length : 0;
        return p.ExitCode == 0 && length > 0
            ? new Result(output, 0, err)
            : new Result(null, p.ExitCode, err);
    }

    /// <summary>Refresh mtime at most daily so the size-cap sweep evicts the
    /// least recently used tracks. Daily, not per hit: mtime is also the
    /// response's Last-Modified.</summary>
    private static void TouchForLru(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(path, now);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Keep the cache under <see cref="MaxCacheBytes"/> (trim the oldest
    /// down to <see cref="TrimToBytes"/>) and clear temp files a crash left.</summary>
    private static void Sweep(string dir)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            var files = new DirectoryInfo(dir).EnumerateFiles().ToList();
            var staleTmpCutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var f in files.Where(f => f.Extension == ".tmp" && f.LastWriteTimeUtc < staleTmpCutoff))
                TryDelete(f.FullName);

            var live = files.Where(f => f.Extension != ".tmp").ToList();
            var total = live.Sum(f => f.Length);
            if (total <= MaxCacheBytes) return;
            foreach (var f in live.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= TrimToBytes) break;
                TryDelete(f.FullName);
                total -= f.Length;
            }
        }
        catch { /* best-effort */ }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
