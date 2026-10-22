using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Animarr.Shared.Models;

namespace Animarr.Web.Services;

/// <summary>
/// EBU R128 loudness of an audio track (plan Этап 7 / 5.5 "LUFS + client-side
/// gain" — what the <c>NormalizeVolume</c> setting needs): integrated
/// loudness, loudness range and true peak from ffmpeg's <c>ebur128</c> filter
/// over the whole track. That decodes the full track (and reads the whole
/// file), so it is measured once per file version and track, in the
/// background, one at a time, and kept by <see cref="FileFactsStore"/>.
/// </summary>
public sealed partial class LoudnessService
{
    /// <summary>The gain a client should apply aims here (streaming-typical).</summary>
    public const double TargetLufs = -16.0;
    /// <summary>…but never pushes the true peak above this.</summary>
    public const double PeakCeilingDbtp = -1.0;

    private static readonly TimeSpan MeasureTimeout = TimeSpan.FromMinutes(20);

    private readonly FileFactsStore _store;
    private readonly MediaProbeCache _probeCache;
    private readonly ILogger<LoudnessService> _logger;

    public LoudnessService(FileFactsStore store, MediaProbeCache probeCache, ILogger<LoudnessService> logger)
    {
        _store      = store;
        _probeCache = probeCache;
        _logger     = logger;
    }

    /// <summary>What is stored per (file version, track).</summary>
    public sealed record Measurement(double? IntegratedLufs, double? LoudnessRangeLu, double? TruePeakDbtp);

    public async Task<LoudnessDto> GetAsync(string fullPath, int track, TimeSpan wait, CancellationToken ct)
    {
        var kind = "loudness1-a" + track.ToString(CultureInfo.InvariantCulture);
        var m = _store.TryGet<Measurement>(kind, fullPath)
                ?? await _store.GetOrComputeAsync(kind, fullPath, token => MeasureAsync(fullPath, track, token),
                       MeasureTimeout, wait, ct);
        if (m is null)
            return new LoudnessDto(track, _store.IsPending(kind, fullPath) ? "pending" : "unknown", null, null, null, null);
        double? gain = m.IntegratedLufs is { } i
            ? Math.Round(Math.Min(TargetLufs - i, m.TruePeakDbtp is { } p ? PeakCeilingDbtp - p : double.MaxValue), 1)
            : null;
        return new LoudnessDto(track, m.IntegratedLufs is null ? "unknown" : "measured",
            m.IntegratedLufs, m.LoudnessRangeLu, m.TruePeakDbtp, gain);
    }

    /// <summary>Number of audio streams in the file (for range checks).</summary>
    public async Task<int> AudioTrackCountAsync(string fullPath, CancellationToken ct)
    {
        var json = await _probeCache.GetStreamsJsonAsync(fullPath, ct);
        return json is null ? 0 : CountAudio().Count(json);
    }

    /// <summary>A full decode of the track: background work in an
    /// AnalysisGate slot, parked while anyone is watching; a full queue = no
    /// measurement now (the next request asks again).</summary>
    private async Task<Measurement?> MeasureAsync(string fullPath, int track, CancellationToken ct)
    {
        try { return await AnalysisGate.Default.RunAsync(t => MeasureNowAsync(fullPath, track, t), park: true, ct); }
        catch (AnalysisBusyException) { return null; }
    }

    private async Task<Measurement?> MeasureNowAsync(string fullPath, int track, CancellationToken ct)
    {
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                RedirectStandardOutput = false,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in new[]
                     {
                         "-hide_banner", "-nostats", "-v", "info", "-nostdin",
                         "-i", fullPath, "-map", $"0:a:{track.ToString(CultureInfo.InvariantCulture)}",
                         "-vn", "-sn", "-dn", "-af", "ebur128=peak=true:framelog=quiet", "-f", "null", "-",
                     })
                psi.ArgumentList.Add(a);
            var started = Stopwatch.StartNew();
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            var err = await stderr;
            if (p.ExitCode != 0) return null;

            // The summary block comes last: "I: -23.1 LUFS", "LRA: 7.2 LU", "Peak: -1.3 dBFS".
            var summary = err.LastIndexOf("Summary:", StringComparison.Ordinal) is var at and >= 0 ? err[at..] : err;
            var m = new Measurement(Number(IntegratedRegex(), summary), Number(RangeRegex(), summary), Number(PeakRegex(), summary));
            _logger.LogInformation("Loudness of {Path} a:{Track}: {I} LUFS, LRA {Lra} LU, peak {Peak} dBTP ({Ms} ms)",
                fullPath, track, m.IntegratedLufs, m.LoudnessRangeLu, m.TruePeakDbtp, started.ElapsedMilliseconds);
            return m.IntegratedLufs is null ? null : m;
        }
    }

    private static double? Number(Regex rx, string text)
    {
        var m = rx.Match(text);
        if (!m.Success) return null;
        var v = m.Groups["v"].Value;
        if (v.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"\bI:\s*(?<v>-?inf|-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.IgnoreCase)]
    private static partial Regex IntegratedRegex();
    [GeneratedRegex(@"\bLRA:\s*(?<v>-?\d+(?:\.\d+)?)\s*LU\b", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();
    [GeneratedRegex(@"\bPeak:\s*(?<v>-?inf|-?\d+(?:\.\d+)?)\s*dBFS", RegexOptions.IgnoreCase)]
    private static partial Regex PeakRegex();
    [GeneratedRegex(@"""codec_type""\s*:\s*""audio""")]
    private static partial Regex CountAudio();
}
