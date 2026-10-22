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
        DateTime ProbedAt,
        ToneMapSupport? ToneMapping = null,
        // VAAPI decodes HEVC Main10 (VAProfileHEVCMain10 / VLD) — gates the
        // 10-bit HEVC → H.264 re-encode on VAAPI hosts.
        bool VaapiHevc10Decode = false,
        // HEVC Main10 ENCODE (hevc_vaapi / hevc_nvenc -profile:v main10) — gates
        // the 10-bit HEVC outputs (Hi10P → HEVC Main10, HDR-keeping downscale).
        // VAAPI: vainfo lists VAProfileHEVCMain10 with an EncSlice entry point
        // (the prod Vega 11 / VCN 1.0 decodes Main10 but has NO Main10 encode).
        // NVENC: a real 0.2 s test encode (Pascal and newer can).
        bool VaapiHevc10Encode = false,
        bool NvencHevc10Encode = false);

    /// <summary>HDR → SDR tone mappers the image's ffmpeg can actually run
    /// here. <see cref="Libplacebo"/> is a real end-to-end probe (VAAPI-derived
    /// Vulkan device + libplacebo on a 10-bit test frame), not just the filter
    /// list — the filter exists in every build but needs a working GPU ICD.</summary>
    public sealed record ToneMapSupport(bool Libplacebo, bool Tonemapx, bool TonemapCuda, string? VulkanDevice);

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
        var vaapi = ProbeVaapi(hwAccels, encoders);
        var nvenc = ProbeNvenc(hwAccels, encoders);
        return new HardwareReport(
            Vaapi:     vaapi,
            Nvenc:     nvenc,
            Qsv:       ProbeQsv(hwAccels, encoders),
            HwAccels:  hwAccels,
            HwEncoders: encoders,
            ProbedAt:   DateTime.UtcNow,
            ToneMapping: ProbeToneMapping(vaapi.Available),
            VaapiHevc10Decode: vaapi.Available
                && Regex.IsMatch(_vainfo, @"VAProfileHEVCMain10\s*:\s*VAEntrypointVLD"),
            VaapiHevc10Encode: vaapi.Available && encoders.Contains("hevc_vaapi")
                && Regex.IsMatch(_vainfo, @"VAProfileHEVCMain10\s*:\s*VAEntrypointEncSlice"),
            NvencHevc10Encode: nvenc.Available && encoders.Contains("hevc_nvenc")
                && RunExitCode("ffmpeg", new[]
                {
                    "-hide_banner", "-v", "error",
                    "-f", "lavfi", "-i", "testsrc2=s=256x144:d=0.2,format=p010le",
                    "-c:v", "hevc_nvenc", "-profile:v", "main10", "-f", "null", "-",
                }, 10000).Ok);
    }

    private ToneMapSupport ProbeToneMapping(bool vaapiAvailable)
    {
        var filters = RunCommand("ffmpeg", new[] { "-hide_banner", "-filters" }, 3000) ?? "";
        bool HasFilter(string name) => Regex.IsMatch(filters, $@"\s{Regex.Escape(name)}\s");
        bool tonemapx = HasFilter("tonemapx");
        bool tonemapCuda = HasFilter("tonemap_cuda");

        // libplacebo only pays off on a real GPU next to the VAAPI encoder: run
        // the exact device setup the VAAPI plan uses on a tiny 10-bit frame.
        // A software Vulkan ICD (llvmpipe / lavapipe) would "work" but be far
        // too slow, so it doesn't count.
        bool libplacebo = false;
        string? device = null;
        if (vaapiAvailable && HasFilter("libplacebo"))
        {
            var (ok, log) = RunExitCode("ffmpeg", new[]
            {
                "-hide_banner", "-v", "verbose",
                "-init_hw_device", "vaapi=va:/dev/dri/renderD128",
                "-init_hw_device", "vulkan=vk@va", "-filter_hw_device", "vk",
                "-f", "lavfi", "-i", "testsrc2=s=256x144:d=0.2,format=yuv420p10le",
                "-vf", $"hwupload,{HdrToneMap.LibplaceboFilter},hwdownload,format=nv12",
                "-f", "null", "-",
            }, 10000);
            var m = Regex.Match(log, @"Device \d+ selected: (.+?) \(", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(log, @"GPU listing:\s*\S*\s*\d+: ([^\r\n(]+)", RegexOptions.IgnoreCase);
            device = m.Success ? m.Groups[1].Value.Trim() : null;
            libplacebo = ok && !(device ?? "").Contains("llvmpipe", StringComparison.OrdinalIgnoreCase);
        }
        return new ToneMapSupport(libplacebo, tonemapx, tonemapCuda, device);
    }

    /// <summary>Runs a command to completion and reports whether it exited 0,
    /// plus its combined output (stderr is where ffmpeg logs).</summary>
    private (bool Ok, string Output) RunExitCode(string cmd, string[] args, int timeoutMs)
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
            if (p is null) return (false, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (false, "");
            }
            Task.WaitAll([stdout, stderr], 1000);
            return (p.ExitCode == 0, stdout.Result + "\n" + stderr.Result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunExitCode failed for {Cmd}", cmd);
            return (false, "");
        }
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

    /// <summary>vainfo output from the last VAAPI probe (decode-profile checks).</summary>
    private string _vainfo = "";

    private EncoderStatus ProbeVaapi(string[] hwAccels, string[] encoders)
    {
        _vainfo = "";
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
        _vainfo = vainfo;
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
    /// instead of throwing — probing missing utilities is normal here.
    ///
    /// Both pipes are read asynchronously BEFORE waiting: the old synchronous
    /// ReadToEnd() blocked until the tool exited, so the timeout never applied
    /// (a wedged vainfo / nvidia-smi on a stuck GPU driver hung startup, since
    /// this runs in the singleton's constructor), and reading stdout then
    /// stderr in sequence could deadlock on a chatty stderr.</summary>
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
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            // Exited: the pipes hit EOF right away unless a grandchild still
            // holds them — bound that wait too.
            if (!Task.WaitAll([stdout, stderr], 1000)) return null;
            return stdout.Result + "\n" + stderr.Result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunCommand failed for {Cmd}", cmd);
            return null;
        }
    }

    private void LogReport(HardwareReport r)
    {
        _logger.LogInformation("HW probe: VAAPI={V} NVENC={N} QSV={Q} HEVC Main10 encode: vaapi={V10} nvenc={N10} HDR tone map: libplacebo={P} tonemapx={X} tonemap_cuda={C}",
            r.Vaapi.Available  ? $"✓ {r.Vaapi.Vendor} {r.Vaapi.DeviceName}".Trim() : "✗",
            r.Nvenc.Available  ? $"✓ {r.Nvenc.DeviceName}" : "✗",
            r.Qsv.Available    ? $"✓ Intel" : "✗",
            r.VaapiHevc10Encode ? "✓" : "✗",
            r.NvencHevc10Encode ? "✓" : "✗",
            r.ToneMapping?.Libplacebo == true ? $"✓ {r.ToneMapping.VulkanDevice}".Trim() : "✗",
            r.ToneMapping?.Tonemapx == true ? "✓" : "✗",
            r.ToneMapping?.TonemapCuda == true ? "✓" : "✗");
    }
}
