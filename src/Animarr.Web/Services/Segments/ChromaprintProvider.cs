using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Cascade level 2: audio fingerprinting (Chromaprint), the same idea as the
/// Jellyfin intro-skipper. An opening/ending is audio SHARED across a season, so
/// we fingerprint this episode and a season peer and look for the longest
/// contiguous matching run — in the head it's the intro, in the tail the credits.
///
/// Expensive (decodes audio with ffmpeg) so it never runs on the cheap/lazy
/// path. Requires ffmpeg built with the chromaprint muxer (jellyfin-ffmpeg has
/// it); on a plain ffmpeg the muxer is missing and this provider yields nothing
/// — the cascade then falls through to the player's 95% fallback. Needs at least
/// one season peer.
/// </summary>
public sealed class ChromaprintProvider(ILogger<ChromaprintProvider> logger) : ISegmentProvider
{
    public SegmentSource Source => SegmentSource.Chromaprint;
    public int Order => 20;
    public bool Cheap => false;   // decodes audio — background pass only

    // Chromaprint params (sample rate 11025, frame 4096, 2/3 overlap) → one
    // fingerprint point ≈ 0.12383 s, i.e. ~8.07 points/sec.
    private const double SampleDuration = 4096.0 / (11025.0 * 3.0);

    private const double HeadWindowSec = 720;   // analyse the first 12 min for the intro
    private const double TailWindowSec = 300;   // analyse the last 5 min for the credits

    private const int    BitThreshold  = 8;     // max differing bits for two points to "match"
    private static readonly int MaxShiftPoints  = (int)(10.0 / SampleDuration);  // ±10 s alignment search
    private static readonly int MinIntroPoints  = (int)(15.0 / SampleDuration);  // ≥15 s run to count
    private static readonly int MinCreditsPoints = (int)(15.0 / SampleDuration);

    // Per-(file,window) fingerprint cache. The provider is scoped to one
    // DetectForItem run, so a peer fingerprinted for episode 1 is reused for
    // episode 2, 3, … — N unique fingerprints per season, not N².
    private readonly Dictionary<string, uint[]> _cache = new();

    public bool CanRun(SegmentEpisodeContext ctx)
        => ctx.SeasonFiles.Count >= 2 && File.Exists(ctx.FilePath);

    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(SegmentEpisodeContext ctx, CancellationToken ct)
    {
        var peers = ctx.SeasonFiles
            .Where(f => !string.Equals(f, ctx.FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .Take(2)
            .ToList();
        if (peers.Count == 0) return Array.Empty<DetectedSegment>();

        var result = new List<DetectedSegment>();

        // ── Intro: shared audio at the head ───────────────────────────────
        var aHead = await GetFingerprintAsync(ctx.FilePath, 0, HeadWindowSec, ct);
        if (aHead.Length > MinIntroPoints)
        {
            foreach (var peer in peers)
            {
                var bHead = await GetFingerprintAsync(peer, 0, HeadWindowSec, ct);
                if (bHead.Length <= MinIntroPoints) continue;
                var (start, len) = LongestCommonRun(aHead, bHead, MinIntroPoints);
                if (len > 0)
                {
                    result.Add(new DetectedSegment(SegmentKind.Intro,
                        start * SampleDuration, (start + len) * SampleDuration));
                    break;   // first peer that yields an intro wins
                }
            }
        }

        // ── Credits: shared audio at the tail ─────────────────────────────
        if (ctx.DurationSec > TailWindowSec)
        {
            var aTailStart = ctx.DurationSec - TailWindowSec;
            var aTail = await GetFingerprintAsync(ctx.FilePath, aTailStart, TailWindowSec, ct);
            if (aTail.Length > MinCreditsPoints)
            {
                foreach (var peer in peers)
                {
                    var peerDur = await MediaProbe.GetDurationAsync(peer, ct);
                    var peerTailStart = peerDur > TailWindowSec ? peerDur - TailWindowSec : 0;
                    var bTail = await GetFingerprintAsync(peer, peerTailStart, TailWindowSec, ct);
                    if (bTail.Length <= MinCreditsPoints) continue;
                    var (start, len) = LongestCommonRun(aTail, bTail, MinCreditsPoints);
                    if (len > 0)
                    {
                        result.Add(new DetectedSegment(SegmentKind.Credits,
                            aTailStart + start * SampleDuration,
                            aTailStart + (start + len) * SampleDuration));
                        break;
                    }
                }
            }
        }

        if (result.Count > 0)
            logger.LogInformation("[Chromaprint] {File} → {Count} segment(s)",
                Path.GetFileName(ctx.FilePath), result.Count);
        return result;
    }

    private async Task<uint[]> GetFingerprintAsync(string file, double ssSec, double durSec, CancellationToken ct)
    {
        var key = $"{file}|{ssSec:0}|{durSec:0}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var fp = await FingerprintAsync(file, ssSec, durSec, ct);
        _cache[key] = fp;
        return fp;
    }

    /// <summary>Extract a raw Chromaprint fingerprint (array of 32-bit points)
    /// from a window of the file's first audio stream via ffmpeg's chromaprint
    /// muxer. Empty array on any failure (missing muxer, decode error).</summary>
    private async Task<uint[]> FingerprintAsync(string file, double ssSec, double durSec, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            var args = new List<string> { "-nostdin", "-v", "error" };
            if (ssSec > 0) { args.Add("-ss"); args.Add(ssSec.ToString("0.###", inv)); }
            args.Add("-i"); args.Add(file);
            args.Add("-t"); args.Add(durSec.ToString("0.###", inv));
            args.Add("-vn");
            args.Add("-map"); args.Add("0:a:0");
            args.Add("-ac"); args.Add("1");
            args.Add("-ar"); args.Add("11025");
            args.Add("-f"); args.Add("chromaprint");
            args.Add("-fp_format"); args.Add("raw");
            args.Add("-");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<uint>();
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            var err  = p.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(copy, err);
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0 || ms.Length < 4)
            {
                logger.LogDebug("[Chromaprint] ffmpeg yielded no fingerprint for {File} (exit {Exit}). " +
                                "Is ffmpeg built with the chromaprint muxer?", Path.GetFileName(file), p.ExitCode);
                return Array.Empty<uint>();
            }

            var bytes = ms.ToArray();
            var n = bytes.Length / 4;
            var fp = new uint[n];
            Buffer.BlockCopy(bytes, 0, fp, 0, n * 4);   // raw = little-endian int32 points (x64/ARM host)
            return fp;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Chromaprint] fingerprint failed for {File}", Path.GetFileName(file));
            return Array.Empty<uint>();
        }
    }

    /// <summary>Longest contiguous run of matching points between two
    /// fingerprints, scanning alignment shifts of ±<see cref="MaxShiftPoints"/>.
    /// Returns the run's start index in <paramref name="a"/> and its length (in
    /// points), or (0,0) when nothing reaches <paramref name="minLen"/>.</summary>
    private static (int Start, int Len) LongestCommonRun(uint[] a, uint[] b, int minLen)
    {
        int bestLen = 0, bestStart = 0;
        for (int shift = -MaxShiftPoints; shift <= MaxShiftPoints; shift++)
        {
            int run = 0, runStart = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int j = i + shift;
                if (j < 0) continue;
                if (j >= b.Length) break;
                if (BitOperations.PopCount(a[i] ^ b[j]) <= BitThreshold)
                {
                    if (run == 0) runStart = i;
                    run++;
                    if (run > bestLen) { bestLen = run; bestStart = runStart; }
                }
                else
                {
                    run = 0;
                }
            }
        }
        return bestLen >= minLen ? (bestStart, bestLen) : (0, 0);
    }
}
