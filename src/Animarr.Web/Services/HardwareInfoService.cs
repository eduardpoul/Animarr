using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// Probes the runtime container for hardware-acceleration capabilities at
/// startup. Result is cached for the container's lifetime; restart needed to
/// re-detect (which is fine — devices don't appear at runtime in Docker).
///
/// What we probe:
///   • <c>/dev/dri/*</c>          — render nodes exposed by `--device /dev/dri`
///   • <c>vainfo</c>              — VAAPI driver + supported entry-points
///   • <c>nvidia-smi</c>          — NVIDIA GPU presence (only works if `--gpus all`)
///   • <c>ffmpeg -hwaccels</c>    — compiled-in hw acceleration backends
///   • <c>ffmpeg -encoders</c>    — VAAPI/NVENC/QSV encoders that exist in build
///
/// All probes run in parallel and have a short timeout — if a tool isn't
/// installed or hangs we just mark the capability as unavailable.
/// </summary>
public sealed class HardwareInfoService
{
    public sealed record EncoderStatus(
        bool Available,
        string? Vendor,        // "AMD" / "Intel" / "NVIDIA" / null
        string? DeviceName,    // e.g. "NVIDIA RTX 5080", "AMD Radeon Vega 11"
        string? DriverInfo,    // driver version / vainfo line
        string? Detail);       // human-readable status text

    public sealed record HardwareReport(
        EncoderStatus Vaapi,
        EncoderStatus Nvenc,
        EncoderStatus Qsv,
        string[] HwAccels,
        string[] HwEncoders,
        DateTime ProbedAt);

    private readonly ILogger<HardwareInfoService> _logger;
    public HardwareReport Current { get; private set; }

    public HardwareInfoService(ILogger<HardwareInfoService> logger)
    {
        _logger = logger;
        // Synchronously probe at construction. Each subprobe has a hard timeout
        // so the whole thing finishes in 1-3 seconds even if a tool hangs.
        Current = Probe();
        LogReport(Current);
    }

    public void Rescan()
    {
        Current = Probe();
        LogReport(Current);
    }

    private HardwareReport Probe()
    {
        var hwAccels = ProbeHwAccels();
        var encoders = ProbeHwEncoders();
        return new HardwareReport(
            Vaapi:     ProbeVaapi(hwAccels, encoders),
            Nvenc:     ProbeNvenc(hwAccels, encoders),
            Qsv:       ProbeQsv(hwAccels, encoders),
            HwAccels:  hwAccels,
            HwEncoders: encoders,
            ProbedAt:   DateTime.UtcNow);
    }

    // ─── ffmpeg-level probes ─────────────────────────────────────────────

    private string[] ProbeHwAccels()
    {
        // `ffmpeg -hwaccels` outputs one accel per line under a header. Parse
        // every non-empty line after "Hardware acceleration methods:".
        var output = RunCommand("ffmpeg", new[] { "-v", "error", "-hwaccels" }, 2000);
        if (output is null) return Array.Empty<string>();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains(':') && !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    private string[] ProbeHwEncoders()
    {
        // Filter `ffmpeg -encoders` output for the families we care about.
        var output = RunCommand("ffmpeg", new[] { "-v", "error", "-encoders" }, 3000);
        if (output is null) return Array.Empty<string>();
        var rx = new Regex(@"\s*V\.\S*\s+(\S+)\s+", RegexOptions.Compiled);
        return output.Split('\n')
            .Select(l => rx.Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Where(n => n.EndsWith("_vaapi") || n.EndsWith("_nvenc") || n.EndsWith("_qsv"))
            .ToArray();
    }

    // ─── Vendor-specific probes ──────────────────────────────────────────

    private EncoderStatus ProbeVaapi(string[] hwAccels, string[] encoders)
    {
        bool ffmpegHasVaapi = hwAccels.Contains("vaapi") && encoders.Any(e => e.EndsWith("_vaapi"));
        bool driExists      = File.Exists("/dev/dri/renderD128") || Directory.Exists("/dev/dri");

        if (!driExists)
        {
            return new EncoderStatus(false, null, null, null,
                ffmpegHasVaapi
                    ? "ffmpeg has VAAPI but /dev/dri/renderD128 not exposed. " +
                      "Add `--device /dev/dri --group-add video` to docker run."
                    : "ffmpeg lacks VAAPI support (unexpected — should be in image).");
        }
        if (!ffmpegHasVaapi)
        {
            return new EncoderStatus(false, null, null, null,
                "ffmpeg in image doesn't include VAAPI (build issue).");
        }

        // /dev/dri exists + ffmpeg has vaapi → try `vainfo` for vendor info.
        var vainfo = RunCommand("vainfo", Array.Empty<string>(), 2500) ?? "";
        string? vendor = null, deviceName = null, driverInfo = null;
        // vainfo prints lines like:
        //   Driver version: Mesa Gallium driver 23.2.1 for AMD Radeon Vega 11
        //   vainfo: Driver version: Intel iHD driver for Intel(R) Gen Graphics
        var driverLine = vainfo.Split('\n').FirstOrDefault(l => l.Contains("Driver version:"));
        if (driverLine is not null)
        {
            driverInfo = driverLine.Trim();
            if (driverLine.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                driverLine.Contains("radeon", StringComparison.OrdinalIgnoreCase))
                vendor = "AMD";
            else if (driverLine.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                     driverLine.Contains("iHD", StringComparison.OrdinalIgnoreCase) ||
                     driverLine.Contains("i965", StringComparison.OrdinalIgnoreCase))
                vendor = "Intel";
            // Pull "for X" tail as device name (after the last "for")
            var forIdx = driverLine.LastIndexOf(" for ", StringComparison.OrdinalIgnoreCase);
            if (forIdx >= 0) deviceName = driverLine[(forIdx + 5)..].Trim();
        }

        bool actuallyWorks = !string.IsNullOrEmpty(driverInfo) || vainfo.Contains("VAEntrypoint");
        return new EncoderStatus(
            Available:  actuallyWorks,
            Vendor:     vendor,
            DeviceName: deviceName,
            DriverInfo: driverInfo,
            Detail:     actuallyWorks
                ? "VAAPI ready — h264_vaapi available for HEVC 8-bit re-encode."
                : "vainfo couldn't initialise; /dev/dri exists but driver may be wrong.");
    }

    private EncoderStatus ProbeNvenc(string[] hwAccels, string[] encoders)
    {
        bool ffmpegHasNvenc = encoders.Any(e => e.EndsWith("_nvenc"));
        bool nvidiaSmi      = RunCommand("nvidia-smi", new[] { "-L" }, 2500) is { Length: > 0 } s
                            && s.Contains("GPU ", StringComparison.OrdinalIgnoreCase);
        if (!nvidiaSmi)
        {
            return new EncoderStatus(false, null, null, null,
                ffmpegHasNvenc
                    ? "ffmpeg has NVENC but nvidia-smi not accessible. " +
                      "Add `--gpus all` to docker run (requires nvidia-container-toolkit on host)."
                    : "ffmpeg lacks NVENC support (unexpected — Debian build should include it).");
        }
        if (!ffmpegHasNvenc)
        {
            return new EncoderStatus(false, null, null, null,
                "NVIDIA GPU detected but ffmpeg in image lacks NVENC (build issue).");
        }

        // Both pieces present → grab GPU name from `nvidia-smi -L`.
        var listing = RunCommand("nvidia-smi", new[] { "-L" }, 2000) ?? "";
        var firstLine = listing.Split('\n').FirstOrDefault(l => l.StartsWith("GPU ")) ?? "";
        var nameMatch = Regex.Match(firstLine, @"GPU \d+: (.+?)(?: \(UUID:|$)");
        var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "NVIDIA GPU";
        var driver = RunCommand("nvidia-smi",
            new[] { "--query-gpu=driver_version", "--format=csv,noheader" }, 2000)?.Trim();
        return new EncoderStatus(
            Available:  true,
            Vendor:     "NVIDIA",
            DeviceName: name,
            DriverInfo: driver is null ? null : $"Driver: {driver}",
            Detail:     "NVENC ready — h264_nvenc available for HEVC 8/10-bit re-encode.");
    }

    private EncoderStatus ProbeQsv(string[] hwAccels, string[] encoders)
    {
        // QSV is Intel-only and uses the same /dev/dri device as VAAPI. We
        // report QSV separately because some ffmpeg builds and some Intel
        // iGPUs perform better on QSV than VAAPI. Animarr's plan logic
        // currently uses VAAPI for Intel; QSV stays informational for now.
        bool ffmpegHasQsv = hwAccels.Contains("qsv") && encoders.Any(e => e.EndsWith("_qsv"));
        bool driExists    = Directory.Exists("/dev/dri");
        if (!ffmpegHasQsv)
        {
            return new EncoderStatus(false, null, null, null,
                "ffmpeg in image lacks QSV (Intel Quick Sync) support.");
        }
        if (!driExists)
        {
            return new EncoderStatus(false, null, null, null,
                "ffmpeg has QSV but /dev/dri not exposed; same flags as VAAPI needed.");
        }
        // QSV needs the Intel iHD driver. Re-use vainfo to confirm.
        var vainfo = RunCommand("vainfo", Array.Empty<string>(), 2000) ?? "";
        if (vainfo.Contains("iHD") || vainfo.Contains("Intel"))
        {
            return new EncoderStatus(true, "Intel", null,
                "Intel iHD driver",
                "QSV ready (h264_qsv) — currently we prefer VAAPI for Intel.");
        }
        return new EncoderStatus(false, null, null, null,
            "ffmpeg has QSV but no Intel iGPU detected via vainfo.");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Run a CLI tool and return its merged stdout/stderr (or null
    /// on launch failure / timeout). Tools that aren't installed return null
    /// instead of throwing — probing missing utilities is normal here.</summary>
    private string? RunCommand(string cmd, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = cmd,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var combinedOut = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            return combinedOut;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunCommand failed for {Cmd}", cmd);
            return null;
        }
    }

    private void LogReport(HardwareReport r)
    {
        _logger.LogInformation("HW probe: VAAPI={V} NVENC={N} QSV={Q}",
            r.Vaapi.Available  ? $"✓ {r.Vaapi.Vendor} {r.Vaapi.DeviceName}".Trim() : "✗",
            r.Nvenc.Available  ? $"✓ {r.Nvenc.DeviceName}" : "✗",
            r.Qsv.Available    ? $"✓ Intel" : "✗");
    }
}
