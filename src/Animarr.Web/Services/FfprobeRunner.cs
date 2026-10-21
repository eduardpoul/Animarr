using System.Diagnostics;

namespace Animarr.Web.Services;

/// <summary>
/// Runs one short-lived <c>ffprobe</c> and hands back its output. Two things
/// every hand-rolled ffprobe call in the app got wrong:
///   • stderr was redirected but never read — a corrupt file that makes ffprobe
///     print more than the 64 KB pipe buffer of warnings blocks it forever;
///     here stderr is drained concurrently with stdout.
///   • no bound at all — a wedged NFS/SMB mount hung playback start (or a
///     background pass) indefinitely; here the run is killed on timeout or when
///     the caller cancels.
/// </summary>
public static class FfprobeRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>Run ffprobe with <paramref name="args"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired (ffprobe killed).</exception>
    /// <exception cref="TimeoutException">ffprobe ran past <paramref name="timeout"/> (killed).</exception>
    public static async Task<Result> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start");
        // Readers run to EOF on their own (no token): the process exiting — or
        // being killed below — closes the pipes and completes them.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"ffprobe gave no answer within {timeout.TotalSeconds:F0}s");
        }
        return new Result(p.ExitCode, await stdout, await stderr);
    }
}
