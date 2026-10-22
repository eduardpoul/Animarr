using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

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
/// One conversion per output: a request for a track that is already being
/// converted awaits that job instead of starting a second ffmpeg over the same
/// file (a player retrying, a second tab, the TV and the web at once), and a
/// finished file is published once — a later writer keeps the published copy.
/// The job belongs to the server, not to the request that started it: a
/// player that gives up and retries joins the running read instead of killing
/// it (bounded by <see cref="ExtractTimeout"/>; progress at Debug).
///
/// The first request for an embedded TEXT track of a container extracts all
/// of its text tracks in that one read (and the original script of each ASS /
/// SSA track), so switching to another language afterwards is a cache hit
/// instead of another full read.
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
    /// <summary>A sweep also follows this many new bytes — one PGS track can
    /// weigh as much as hundreds of text tracks.</summary>
    private const long SweepEveryBytes  = 64L * 1024 * 1024;
    private static int _writesSinceSweep;
    private static long _bytesSinceSweep;
    private static int _sweeping;

    /// <summary><paramref name="Path"/> = the cached track; null = conversion
    /// failed (<paramref name="ExitCode"/> / <paramref name="Error"/> = ffmpeg's).
    /// <paramref name="Charset"/> = the source encoding when it had to be
    /// converted to UTF-8 first (null = used as is / served from cache).
    /// <paramref name="FailureCode"/> (failures only): <see cref="ImageSubtitle"/>
    /// = a bitmap track, refused before any ffmpeg run; <see cref="ConvertFailed"/>
    /// = ffmpeg could not convert it; <see cref="NotPgs"/> = a <c>.sup</c> was
    /// asked of a track that isn't PGS.</summary>
    public sealed record Result(string? Path, int ExitCode, string Error, string? Charset = null,
        string? FailureCode = null);

    public const string ImageSubtitle = "image_subtitle";
    public const string ConvertFailed = "convert_failed";
    public const string NotPgs        = "not_pgs";
    /// <summary>The analysis queue (AnalysisGate) is full — try again later.</summary>
    public const string Busy          = "analysis_busy";

    /// <summary>Failed conversions per cache key (= source version + track +
    /// format), so a bitmap or broken track isn't re-read in full on every
    /// open. In memory only, and they expire: a failure from a file still
    /// being written is retried later anyway (its size, so its key, changes).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Result Result, DateTime Until)> Failures = new();
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);

    /// <summary>Longest one extraction may take — a full read of a 50 GB remux
    /// off a slow disk is minutes; past this it is a stuck share, not a read.</summary>
    public static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Set at startup (Program.cs); a static helper has no DI.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Conversions running now, per cache file: later callers await
    /// the first one's task.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<Result>>> Running =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The job for <paramref name="key"/>: the running one, else
    /// <paramref name="work"/> started now, on its own token — bounded by
    /// <see cref="ExtractTimeout"/>, never by a request (the caller only stops
    /// waiting, see <see cref="Join"/>). It leaves the map when it ends.</summary>
    private static Task<Result> Shared(string key, string what, Func<CancellationToken, Task<Result>> work)
    {
        Lazy<Task<Result>>? mine = null;
        mine = new Lazy<Task<Result>>(async () =>
        {
            using var cts = new CancellationTokenSource(ExtractTimeout);
            try { return await work(cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Logger.LogWarning("Subtitle extraction of {What} gave up after {Minutes} min", what, ExtractTimeout.TotalMinutes);
                return Remember(key, new Result(null, -1, "The extraction timed out.", FailureCode: ConvertFailed));
            }
            finally { Running.TryRemove(new KeyValuePair<string, Lazy<Task<Result>>>(key, mine!)); }
        });
        return Running.GetOrAdd(key, mine).Value;
    }

    /// <summary>Await a shared job; <paramref name="ct"/> (the request) ends
    /// only this wait — the job runs on for the next caller.</summary>
    private static Task<Result> Join(Task<Result> job, CancellationToken ct) => job.WaitAsync(ct);

    /// <summary>Move a finished temp file to its cache name. A copy already
    /// published there (another writer got there first) wins: it is complete
    /// by construction, may be open for a response, and is the same content.</summary>
    private static void Publish(string tmp, string cached)
    {
        try { File.Move(tmp, cached, overwrite: false); }
        catch (IOException) when (File.Exists(cached)) { TryDelete(tmp); }
        catch (UnauthorizedAccessException) when (File.Exists(cached)) { TryDelete(tmp); }
    }

    private static Result? KnownFailure(string cached)
    {
        if (!Failures.TryGetValue(cached, out var f)) return null;
        if (f.Until > DateTime.UtcNow) return f.Result;
        Failures.TryRemove(cached, out _);
        return null;
    }

    private static Result Remember(string cached, Result failure)
    {
        if (Failures.Count > 4096) Failures.Clear();
        Failures[cached] = (failure, DateTime.UtcNow + FailureTtl);
        return failure;
    }

    /// <param name="outFormat">ffmpeg muxer: <c>webvtt</c>, <c>ass</c> or
    /// <c>srt</c> (DLNA renderers).</param>
    /// <param name="codecLookup">ffprobe <c>codec_name</c> of subtitle stream
    /// <paramref name="track"/>: a bitmap track is refused before ffmpeg runs,
    /// an ASS / SSA one served as the original script, and on the non-UTF-8
    /// fallback for an embedded track it picks the muxer its raw packets are
    /// copied out with.</param>
    /// <param name="subtitleCodecs">ffprobe <c>codec_name</c> of every
    /// subtitle stream of the file, in <c>s:N</c> order. Given (with
    /// <paramref name="codecLookup"/>), a text track is extracted together with
    /// all the file's other text tracks in one read.</param>
    public static async Task<Result> GetOrExtractAsync(string sourcePath, int track, string outFormat,
        string cacheRoot, CancellationToken ct, Func<CancellationToken, Task<string?>>? codecLookup = null,
        Func<CancellationToken, Task<IReadOnlyList<string?>>>? subtitleCodecs = null)
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
        if (KnownFailure(cached) is { } failed) return failed;

        // One probe lookup per request, however many steps below ask.
        if (codecLookup is not null)
        {
            var lookup = codecLookup;
            Task<string?>? memo = null;
            codecLookup = c => memo ??= lookup(c);
        }

        // A bitmap track (PGS, VobSub, DVB) can't become text: ffmpeg would
        // read the whole container only to fail at the muxer.
        if (!SubtitleCharset.IsTextSubtitleFile(sourcePath) && codecLookup is not null
            && await codecLookup(ct) is { } sourceCodec && SubtitleCodecs.IsImage(sourceCodec))
            return Remember(cached, new Result(null, -1,
                $"Bitmap subtitle ({sourceCodec}) can't be converted to text.", FailureCode: ImageSubtitle));

        Task<Result> One() => Shared(cached, $"{sourcePath} s:{track} → {outFormat}",
            t => ConvertOneAsync(sourcePath, track, outFormat, dir, cached, fi, codecLookup, t));

        // An embedded text track: every text track of the file in one read
        // first — then this one is normally a cache hit.
        if (subtitleCodecs is not null && codecLookup is not null && !SubtitleCharset.IsTextSubtitleFile(sourcePath)
            && AllTracksFormat(outFormat, await codecLookup(ct)) is { } allFormat)
        {
            async Task<Result> AllThenOne()
            {
                await AllTextTracksAsync(sourcePath, fi, dir, allFormat, subtitleCodecs);
                return File.Exists(cached) ? new Result(cached, 0, "") : await One();
            }
            return await Join(AllThenOne(), ct);
        }
        return await Join(One(), ct);
    }

    /// <summary>The format the all-tracks read writes for a request in
    /// <paramref name="outFormat"/> of a track with <paramref name="codec"/>;
    /// null = no all-tracks read (a bitmap track, or a text track asked as
    /// ASS that isn't ASS — the burn-in's conversion).</summary>
    private static string? AllTracksFormat(string outFormat, string? codec)
    {
        if (!SubtitleCodecs.IsText(codec)) return null;
        return outFormat switch
        {
            "webvtt" or "srt" => outFormat,
            "ass" when SubtitleCodecs.IsAss(codec) => "webvtt",   // the ASS copies come along
            _ => null,
        };
    }

    /// <summary>All-tracks reads already made per file version + format, so a
    /// track that failed in one isn't the reason for another full read.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> AllTracksTried =
        new(StringComparer.OrdinalIgnoreCase);

    private static async Task AllTextTracksAsync(string sourcePath, FileInfo fi, string dir, string format,
        Func<CancellationToken, Task<IReadOnlyList<string?>>> subtitleCodecs)
    {
        var key = $"all|{sourcePath}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{format}";
        if (AllTracksTried.TryGetValue(key, out var until) && until > DateTime.UtcNow) return;
        await Shared(key, $"{sourcePath} all text tracks → {format}",
            t => ExtractAllTextTracksAsync(sourcePath, fi, dir, format, subtitleCodecs, key, t));
    }

    /// <summary>
    /// One ffmpeg over the container writing every text track not cached yet:
    /// <c>-map 0:s:N -f &lt;format&gt;</c> per track, plus <c>-c:s copy -f ass</c>
    /// for each ASS / SSA track (its original script). Only a clean run is
    /// published. When ffmpeg reports text that isn't UTF-8 it can't say which
    /// track, so the converted text is dropped (the ASS copies are re-encoded
    /// as usual) and those tracks take the one-track path with its charset
    /// fallback; a track already known to be in a legacy code page is left out.
    /// </summary>
    private static async Task<Result> ExtractAllTextTracksAsync(string sourcePath, FileInfo fi, string dir, string format,
        Func<CancellationToken, Task<IReadOnlyList<string?>>> subtitleCodecs, string key, CancellationToken ct)
    {
        var codecs = await subtitleCodecs(ct);
        var outputs = new List<(string Cached, string Tmp, bool AssCopy)>();
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-i", sourcePath };
        void Add(int index, string fmt, bool assCopy)
        {
            var cached = CachedPath(dir, sourcePath, index, fmt, fi);
            if (File.Exists(cached) || Running.ContainsKey(cached) || KnownFailure(cached) is not null) return;
            var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
            outputs.Add((cached, tmp, assCopy));
            args.AddRange(assCopy
                ? ["-map", $"0:s:{index}", "-c:s", "copy", "-f", "ass", "-y", tmp]
                : ["-map", $"0:s:{index}", "-f", fmt, "-y", tmp]);
        }
        for (int i = 0; i < codecs.Count; i++)
        {
            if (!SubtitleCodecs.IsText(codecs[i])) continue;
            if (SubtitleCharset.TryGetCached(SubtitleCharset.CacheKey(sourcePath, i), out var cs) && cs.NeedsConversion)
                continue;
            Add(i, format, assCopy: false);
            if (SubtitleCodecs.IsAss(codecs[i])) Add(i, "ass", assCopy: true);
        }
        if (outputs.Count == 0) return new Result(null, 0, "");
        try
        {
            var r = await RunAsync(args, outputs[0].Tmp, ct);
            if (r.FailureCode == Busy) return r;   // not tried: the next request may
            if (AllTracksTried.Count > 4096) AllTracksTried.Clear();
            AllTracksTried[key] = DateTime.UtcNow + FailureTtl;
            if (r.ExitCode != 0)
            {
                Logger.LogDebug("All-tracks subtitle read of {Path} failed (exit {Exit}) — tracks go one by one", sourcePath, r.ExitCode);
                return r;
            }
            bool legacy = r.Error.Contains("Invalid UTF-8", StringComparison.OrdinalIgnoreCase);
            int published = 0;
            foreach (var o in outputs)
            {
                if (!File.Exists(o.Tmp) || new FileInfo(o.Tmp).Length == 0) continue;
                if (o.AssCopy) await EnsureUtf8Async(o.Tmp, ct);
                else if (legacy) continue;
                Publish(o.Tmp, o.Cached);
                NoteWrite(dir, o.Cached);
                published++;
            }
            Logger.LogDebug("All-tracks subtitle read of {Path}: {Published} of {Outputs} outputs cached{Legacy}", sourcePath,
                published, outputs.Count, legacy ? " (non-UTF-8 text: conversions left to the one-track path)" : "");
            return new Result(null, 0, "");
        }
        finally
        {
            foreach (var o in outputs) TryDelete(o.Tmp);
        }
    }

    /// <summary>The conversion behind <see cref="GetOrExtractAsync"/> — run
    /// once per cache file at a time (<see cref="Shared"/>).</summary>
    private static async Task<Result> ConvertOneAsync(string sourcePath, int track, string outFormat, string dir,
        string cached, FileInfo fi, Func<CancellationToken, Task<string?>>? codecLookup, CancellationToken ct)
    {
        // A job that queued behind another one may find the file made.
        if (File.Exists(cached)) return new Result(cached, 0, "");

        // An ASS / SSA source asked for as ASS is served as the original
        // script (styles, typesetting, [Fonts] intact) instead of a round trip
        // through ffmpeg's ASS decoder + encoder.
        if (outFormat == "ass")
        {
            var original = await TryOriginalAssAsync(sourcePath, track, cached, fi, codecLookup, ct);
            if (original is not null) return original;
        }

        // Unique temp names; Publish keeps whichever copy landed first.
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

            // A full analysis queue is "not now", not a broken track.
            if (r.FailureCode == Busy) return r;
            if (r.Path is null) return Remember(cached, r with { FailureCode = ConvertFailed });
            Publish(tmp, cached);
            NoteWrite(dir, cached);
            return r with { Path = cached };
        }
        finally
        {
            foreach (var t in temps) TryDelete(t);
        }
    }

    /// <summary>
    /// A PGS track as a raw <c>.sup</c> stream (what libbitsub reads):
    /// <c>-c:s copy -f sup</c>, cached like the text conversions. A standalone
    /// <c>.sup</c> file is served as itself. Anything that isn't PGS is refused
    /// up front (<see cref="NotPgs"/>) — VobSub has no ffmpeg muxer that a web
    /// renderer reads.
    /// </summary>
    public static async Task<Result> GetOrExtractSupAsync(string sourcePath, int track, string cacheRoot,
        Func<CancellationToken, Task<string?>> codecLookup, CancellationToken ct)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".sup", StringComparison.OrdinalIgnoreCase))
            return track == 0 ? new Result(sourcePath, 0, "") : new Result(null, -1, "A .sup file has one track.", FailureCode: NotPgs);

        var dir = Path.Combine(cacheRoot, "subtitles");
        Directory.CreateDirectory(dir);
        var fi     = new FileInfo(sourcePath);
        var cached = CachedPath(dir, sourcePath, track, "sup", fi);
        if (File.Exists(cached))
        {
            TouchForLru(cached);
            return new Result(cached, 0, "");
        }
        if (KnownFailure(cached) is { } failed) return failed;

        var codec = await codecLookup(ct);
        if (!SubtitleCodecs.IsPgs(codec))
            return Remember(cached, new Result(null, -1,
                codec is null ? "No such subtitle track." : $"Subtitle track {track} is {codec}, not PGS.", FailureCode: NotPgs));

        return await Join(Shared(cached, $"{sourcePath} s:{track} → sup", async t =>
        {
            if (File.Exists(cached)) return new Result(cached, 0, "");
            var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
            try
            {
                var r = await RunAsync(
                [
                    "-hide_banner", "-loglevel", "warning",
                    "-i", sourcePath, "-map", $"0:s:{track}", "-c:s", "copy", "-f", "sup", "-y", tmp,
                ], tmp, t);
                if (r.FailureCode == Busy) return r;
                if (r.Path is null) return Remember(cached, r with { FailureCode = ConvertFailed });
                Publish(tmp, cached);
                NoteWrite(dir, cached);
                return r with { Path = cached };
            }
            finally { TryDelete(tmp); }
        }), ct);
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
        var ext = outFormat switch { "ass" => "ass", "sup" => "sup", "srt" => "srt", _ => "vtt" };
        return Path.Combine(dir, $"{hash}.{ext}");
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
            Publish(tmp, cached);
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
            .Where(x => !File.Exists(x.Cached) && !Running.ContainsKey(x.Cached))
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
                Publish(x.Tmp, x.Cached);
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
        // A conversion reads the whole container: one of the AnalysisGate's
        // slots, not one process per request.
        try { return await AnalysisGate.Default.RunAsync(t => RunNowAsync(args, output, t), park: false, ct); }
        catch (AnalysisBusyException) { return new Result(null, -1, "analysis queue full", FailureCode: Busy); }
    }

    private static async Task<Result> RunNowAsync(IEnumerable<string> args, string output, CancellationToken ct)
    {
        var list = args.ToList();
        int input = list.IndexOf("-i");
        var what = $"{(input >= 0 && input + 1 < list.Count ? list[input + 1] : "?")} ({list.Count(a => a == "-y")} output(s))";
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            // Outputs go to files; stdout carries -progress (Debug log).
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[] { "-nostats", "-progress", "pipe:1", "-stats_period", "15" }) psi.ArgumentList.Add(a);
        foreach (var a in list) psi.ArgumentList.Add(a);

        var clock = Stopwatch.StartNew();
        Logger.LogDebug("Subtitle extraction started: {What}", what);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        // Drain both pipes alongside the run — a full pipe stalls ffmpeg.
        var stderr = p.StandardError.ReadToEndAsync();
        var progress = LogProgressAsync(p.StandardOutput, what, clock);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch
        {
            // Timed out (or worse): stop the full-file read; the caller drops
            // the partial output.
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw;
        }
        var err = await stderr;
        try { await progress.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        Logger.LogDebug("Subtitle extraction finished: {What}, exit {Exit} after {Seconds:0.0} s", what, p.ExitCode,
            clock.Elapsed.TotalSeconds);

        var length = File.Exists(output) ? new FileInfo(output).Length : 0;
        return p.ExitCode == 0 && length > 0
            ? new Result(output, 0, err)
            : new Result(null, p.ExitCode, err);
    }

    /// <summary>ffmpeg's <c>-progress</c> blocks → one Debug line each: how far
    /// into the file the read is.</summary>
    private static async Task LogProgressAsync(StreamReader stdout, string what, Stopwatch clock)
    {
        try
        {
            string? at = null;
            while (await stdout.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time=", StringComparison.Ordinal)) at = line[9..];
                else if (line.StartsWith("progress=continue", StringComparison.Ordinal) && at is not null)
                    Logger.LogDebug("Subtitle extraction of {What}: at {At} after {Seconds:0} s", what, at,
                        clock.Elapsed.TotalSeconds);
            }
        }
        catch { /* diagnostics only */ }
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

    /// <summary>A new file in the cache (a text conversion or a <c>.sup</c>):
    /// a sweep every <see cref="SweepEveryWrites"/> files or
    /// <see cref="SweepEveryBytes"/> bytes, whichever comes first.</summary>
    private static void NoteWrite(string dir, string file)
    {
        long length = 0;
        try { length = new FileInfo(file).Length; } catch { }
        bool byCount = Interlocked.Increment(ref _writesSinceSweep) >= SweepEveryWrites;
        bool byBytes = Interlocked.Add(ref _bytesSinceSweep, length) >= SweepEveryBytes;
        if (!byCount && !byBytes) return;
        Interlocked.Exchange(ref _writesSinceSweep, 0);
        Interlocked.Exchange(ref _bytesSinceSweep, 0);
        _ = Task.Run(() => Sweep(dir));
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
