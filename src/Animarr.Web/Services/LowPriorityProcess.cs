using System.Diagnostics;

namespace Animarr.Web.Services;

/// <summary>
/// Starts background child processes (chromaprint / black-frame / trickplay
/// ffmpeg, the embedded llama-server) already deprioritised, so a library pass
/// never fights a live transcode or a Direct Play read for CPU or disk.
///
/// On Linux (the Docker image) the command is launched through
/// <c>nice -n N [ionice -c 3 -t] cmd …</c>. Both tools exec the target, so the
/// PID, pipes and <c>Kill(entireProcessTree)</c> behave exactly as before — but
/// the priority is in place from the first instruction, which a
/// <see cref="Process.PriorityClass"/> set after <c>Start</c> can't guarantee
/// (ffmpeg's decoder threads may already exist and keep nice 0). The idle I/O
/// class matters most on HDD/NAS libraries: a full-file sprite or fingerprint
/// read then only gets the disk when the player isn't using it. <c>-t</c> makes
/// ionice run the command even where the I/O class can't be set (seccomp,
/// exotic schedulers) instead of failing it.
///
/// Elsewhere (Windows dev boxes, or a Linux without the tools) it falls back to
/// <see cref="ProcessPriorityClass.BelowNormal"/> right after start.
/// </summary>
public static class LowPriorityProcess
{
    private sealed record Tools(string? Nice, string? Ionice);

    // Probed once per process: two cheap "true" runs on first use.
    private static readonly Lazy<Tools> _tools = new(Probe);

    /// <summary>Start <paramref name="psi"/> deprioritised. <paramref name="niceness"/>
    /// is the Linux nice increment; <paramref name="idleIo"/> adds the idle I/O
    /// class (use for full-file readers, not for latency-sensitive work).</summary>
    public static Process? Start(ProcessStartInfo psi, int niceness = 10, bool idleIo = true)
    {
        var wrapped = Wrap(psi, niceness, idleIo);
        var p = Process.Start(psi);
        if (p is not null && !wrapped) LowerAfterStart(p);
        return p;
    }

    /// <summary>Rewrite <paramref name="psi"/> to launch through nice/ionice.
    /// Returns false (psi untouched) when the tools aren't available — the caller
    /// should then <see cref="LowerAfterStart"/> the started process.</summary>
    public static bool Wrap(ProcessStartInfo psi, int niceness = 10, bool idleIo = true)
    {
        var tools = _tools.Value;
        // ArgumentList only — a raw Arguments string can't be prefixed safely.
        if (tools.Nice is null || !string.IsNullOrEmpty(psi.Arguments)) return false;

        var prefix = new List<string> { "-n", niceness.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (idleIo && tools.Ionice is not null)
        {
            prefix.Add(tools.Ionice);
            prefix.Add("-c"); prefix.Add("3");
            prefix.Add("-t");
        }
        prefix.Add(psi.FileName);
        for (int i = prefix.Count - 1; i >= 0; i--) psi.ArgumentList.Insert(0, prefix[i]);
        psi.FileName = tools.Nice;
        return true;
    }

    /// <summary>Fallback for platforms without nice: BelowNormal right after start.</summary>
    public static void LowerAfterStart(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* exited already / platform-dependent */ }
    }

    private static Tools Probe()
    {
        if (!OperatingSystem.IsLinux()) return new Tools(null, null);
        var nice = FindOnPath("nice");
        if (nice is null) return new Tools(null, null);
        var ionice = FindOnPath("ionice");
        // Verify once that the exact wrapper we'll use actually runs a command
        // (a broken/busybox ionice without -t must not swallow every ffmpeg).
        if (ionice is not null && RunsOk(nice, "-n", "10", ionice, "-c", "3", "-t", "true"))
            return new Tools(nice, ionice);
        return RunsOk(nice, "-n", "10", "true") ? new Tools(nice, null) : new Tools(null, null);
    }

    private static bool RunsOk(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = file,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            Task.WaitAll([o, e], 1000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
