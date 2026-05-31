using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

public enum EmbeddedState { Stopped, Starting, Ready, Failed }

/// <summary>Snapshot of the embedded llama-server child process.</summary>
public sealed record EmbeddedRuntimeStatus(
    EmbeddedState State, string? ModelId, string? ModelFile,
    int Port, bool GpuActive, string GpuMode, string? Error, DateTime? ReadyAt);

/// <summary>Live progress of the single in-flight model download.</summary>
public sealed record DownloadProgress(
    string? ModelId, string? FileName, long BytesReceived, long? TotalBytes,
    double Percent, string Phase, string? Error);

/// <summary>
/// Supervises the built-in ("embedded") llama.cpp provider: it owns a single
/// <c>llama-server</c> child process bound to loopback, downloads GGUF weights
/// from Hugging Face onto the data volume, and exposes the loopback port that
/// <see cref="MicrosoftAiLlmService"/> points its OpenAI-compatible client at.
///
/// Singleton + hosted service (one server per container), modelled on
/// <see cref="IdentificationQueueProcessorService"/>. Scoped services
/// (<see cref="IAppConfigService"/>) are resolved per-call via
/// <see cref="IServiceScopeFactory"/>, exactly like
/// <see cref="CategoryClassifierService"/>.
///
/// GPU (Vulkan) is opt-in via the <c>ANIMARR_LLM_VULKAN=1</c> env flag; the
/// container entrypoint installs the Vulkan userspace at boot. If the flag is
/// unset, the device is absent, or the Vulkan binary can't load its libs, the
/// service silently falls back to the CPU binary.
/// </summary>
public sealed class EmbeddedLlamaService : IHostedService
{
    private const int    MaxModelGiB  = 20;          // safety cap on Content-Length
    private const int    MaxRestarts  = 5;           // consecutive crash cap before parking Failed
    private const int    DefaultPort  = 8091;
    private const int    ReadyTimeoutSec = 180;
    private const int    DefaultIdleTimeoutSec = 120;  // stop the child after this much LLM inactivity (0 = never)
    private const int    IdleCheckIntervalSec  = 20;

    private static readonly Regex RepoRx = new(@"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex FileRx = new(@"^[A-Za-z0-9._-]+\.gguf$",          RegexOptions.Compiled);

    private readonly IHttpClientFactory   _httpFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModelPaths           _modelPaths;
    private readonly ILogger<EmbeddedLlamaService> _logger;

    // ── Process state (mutations serialised by _procLock) ──────────────────────
    private readonly SemaphoreSlim _procLock = new(1, 1);
    private Process? _process;
    private CancellationTokenSource _lifetimeCts = new();
    private volatile EmbeddedState _state = EmbeddedState.Stopped;
    private volatile string? _modelId;
    private volatile string? _modelFile;
    private volatile string? _error;
    private DateTime? _readyAt;
    private int _effectivePort = DefaultPort;
    private string _gpuMode = "cpu";
    private bool   _gpuActive;
    private bool   _userStopRequested;
    private int    _consecutiveFailures;
    private DateTime _lastActivityAt = DateTime.UtcNow;

    private readonly object _stderrLock = new();
    private readonly Queue<string> _stderrRing = new();

    // ── Download state (single in-flight, guarded by _downloadLock) ─────────────
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private CancellationTokenSource? _downloadCts;
    private volatile DownloadProgress? _currentDownload;

    public EmbeddedLlamaService(
        IHttpClientFactory httpFactory,
        IServiceScopeFactory scopeFactory,
        ModelPaths modelPaths,
        ILogger<EmbeddedLlamaService> logger)
    {
        _httpFactory  = httpFactory;
        _scopeFactory = scopeFactory;
        _modelPaths   = modelPaths;
        _logger       = logger;
    }

    // ── Public surface ──────────────────────────────────────────────────────────

    public int Port => _effectivePort;

    public EmbeddedRuntimeStatus Status =>
        new(_state, _modelId, _modelFile, _effectivePort, _gpuActive, _gpuMode, _error, _readyAt);

    public DownloadProgress CurrentDownload =>
        _currentDownload ?? new DownloadProgress(null, null, 0, null, 0, "idle", null);

    // ── IHostedService ───────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = new CancellationTokenSource();
        DecideGpu();
        // Non-blocking: never delay app startup on a 60s model load.
        _ = Task.Run(BootAsync);
        _ = Task.Run(IdleMonitorAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }
        try { _downloadCts?.Cancel(); } catch { /* ignore */ }
        await _procLock.WaitAsync(cancellationToken);
        try { KillLocked(); }
        finally { _procLock.Release(); }
    }

    private async Task BootAsync()
    {
        try
        {
            var cfg = await ReadConfigAsync(_lifetimeCts.Token);
            // Lazy lifecycle: do NOT launch at boot. The child starts on the first
            // LLM request (EnsureRunningAsync) and idle-stops afterwards, so it
            // never holds RAM/VRAM/CPU while no LLM work is running.
            if (cfg.Provider == "embedded" && cfg.ModelFile is null)
                SetState(EmbeddedState.Stopped, "No model selected — pick & download one in Settings → AI / LLM.");
            else
                SetState(EmbeddedState.Stopped, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedded llama boot check failed");
        }
    }

    /// <summary>Background loop: stops the child after a stretch of no LLM activity
    /// so it frees model RAM / GPU memory while idle. The next LLM request lazily
    /// relaunches it. Set llm.embedded_idle_timeout_sec to 0 to disable.</summary>
    private async Task IdleMonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IdleCheckIntervalSec));
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token))
            {
                if (_state != EmbeddedState.Ready) continue;
                if (_currentDownload is { Phase: "downloading" }) continue;

                int idleSec;
                try { idleSec = await ReadIdleTimeoutAsync(_lifetimeCts.Token); }
                catch { continue; }
                if (idleSec <= 0) continue;                                        // disabled
                if ((DateTime.UtcNow - _lastActivityAt).TotalSeconds < idleSec) continue;

                await _procLock.WaitAsync(_lifetimeCts.Token);
                try
                {
                    // Re-check under the lock — a request may have arrived meanwhile.
                    if (_state == EmbeddedState.Ready
                        && _currentDownload is not { Phase: "downloading" }
                        && (DateTime.UtcNow - _lastActivityAt).TotalSeconds >= idleSec)
                    {
                        _logger.LogInformation("Embedded llama idle {Sec}s — stopping to free RAM/GPU.", idleSec);
                        KillLocked();   // → Stopped; the next LLM call relaunches lazily
                    }
                }
                finally { _procLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* app shutdown */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Idle monitor stopped"); }
    }

    private async Task<int> ReadIdleTimeoutAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        return await cfg.GetAsync<int>(AppConfigKeys.LlmEmbeddedIdleTimeout, DefaultIdleTimeoutSec, ct);
    }

    // ── Lifecycle used by MicrosoftAiLlmService + endpoints ─────────────────────

    /// <summary>Ensure the child is up for the currently-selected model. Lazily
    /// launches if provider==embedded and a model file exists. Returns true once
    /// the server reports healthy within <paramref name="timeout"/>.</summary>
    public async Task<bool> EnsureRunningAsync(TimeSpan timeout, CancellationToken ct)
    {
        _lastActivityAt = DateTime.UtcNow;  // touch: every LLM request resets the idle clock
        if (_state == EmbeddedState.Ready && IsProcAlive()) return true;

        await _procLock.WaitAsync(ct);
        try
        {
            if (_state == EmbeddedState.Ready && IsProcAlive()) return true;
            if (!IsProcAlive())
            {
                var cfg = await ReadConfigAsync(ct);
                if (cfg.Provider != "embedded") return false;
                if (cfg.ModelFile is null)
                {
                    SetState(EmbeddedState.Stopped, "No model selected — pick & download one.");
                    return false;
                }
                LaunchFromConfigLocked(cfg);
            }
        }
        finally { _procLock.Release(); }

        return await WaitUntilReadyAsync(timeout, ct);
    }

    /// <summary>Kill + relaunch the currently-selected model.</summary>
    public async Task<EmbeddedRuntimeStatus> RestartAsync(CancellationToken ct)
    {
        await RestartInternalAsync(ct);
        await WaitUntilReadyAsync(TimeSpan.FromSeconds(ReadyTimeoutSec), ct);
        return Status;
    }

    /// <summary>Persist a new selected model, then restart the child to load it.</summary>
    public async Task<EmbeddedRuntimeStatus> SwitchModelAsync(string modelId, string fileName, CancellationToken ct)
    {
        await SaveSelectedModelAsync(modelId, fileName, repo: null, ct);
        return await RestartAsync(ct);
    }

    private async Task RestartInternalAsync(CancellationToken ct)
    {
        await _procLock.WaitAsync(ct);
        try
        {
            KillLocked();
            _userStopRequested = false;
            _consecutiveFailures = 0;
            var cfg = await ReadConfigAsync(ct);
            if (cfg.Provider == "embedded" && cfg.ModelFile is not null)
                LaunchFromConfigLocked(cfg);
            else
                SetState(EmbeddedState.Stopped, cfg.ModelFile is null ? "No model selected." : null);
        }
        finally { _procLock.Release(); }
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            switch (_state)
            {
                case EmbeddedState.Ready:  return true;
                case EmbeddedState.Failed: return false;
                case EmbeddedState.Stopped: return false;
            }
            try { await Task.Delay(200, ct); } catch { return _state == EmbeddedState.Ready; }
        }
        return _state == EmbeddedState.Ready;
    }

    // ── Launch / kill (caller holds _procLock) ──────────────────────────────────

    private void LaunchFromConfigLocked(EmbeddedConfig cfg)
    {
        // Every launch makes future exits "unexpected" until a new kill flips
        // this — otherwise a stale stop flag would suppress crash-restarts.
        _userStopRequested = false;

        var path = cfg.ModelFile is null ? null : _modelPaths.ResolveModelFile(cfg.ModelFile);
        if (path is null || !File.Exists(path))
        {
            SetState(EmbeddedState.Failed, $"Model file '{cfg.ModelFile}' not found under the models directory.");
            return;
        }

        var binary = _gpuMode == "vulkan" ? VulkanBinary : CpuBinary;
        if (!File.Exists(binary))
        {
            SetState(EmbeddedState.Failed, $"llama-server binary not found at {binary}.");
            return;
        }

        var ngl  = _gpuMode == "vulkan" ? (cfg.NglConfig <= 0 ? 999 : cfg.NglConfig) : 0;
        var port = PickFreePort(cfg.Port <= 0 ? DefaultPort : cfg.Port);
        _effectivePort = port;

        var psi = new ProcessStartInfo
        {
            FileName               = binary,
            UseShellExecute        = false,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            CreateNoWindow         = true,
        };
        SetLibraryPath(psi, binary);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--host");  psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");  psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-ngl");    psi.ArgumentList.Add(ngl.ToString());
        psi.ArgumentList.Add("-c");      psi.ArgumentList.Add(cfg.Ctx.ToString());
        psi.ArgumentList.Add("-a");      psi.ArgumentList.Add("embedded");

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            lock (_stderrLock) _stderrRing.Clear();
            proc.ErrorDataReceived  += (_, e) => CaptureStderr(e.Data);
            proc.OutputDataReceived += (_, e) => CaptureStderr(e.Data);
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            SetState(EmbeddedState.Failed, $"Failed to launch llama-server: {ex.Message}");
            return;
        }

        _process    = proc;
        _modelId    = cfg.ModelId;
        _modelFile  = cfg.ModelFile;
        _readyAt    = null;
        SetState(EmbeddedState.Starting, null);
        _logger.LogInformation("Embedded llama-server starting: model={Model} port={Port} ngl={Ngl} ({Mode})",
            cfg.ModelFile, port, ngl, _gpuMode);

        _ = MonitorAsync(proc);
        _ = ProbeReadyAsync(proc, port);
    }

    private void KillLocked()
    {
        _userStopRequested = true;
        var proc = _process;
        _process = null;
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Killing llama-server child failed (likely already gone)"); }
        finally { proc.Dispose(); }
        if (_state != EmbeddedState.Failed) SetState(EmbeddedState.Stopped, null);
    }

    private async Task ProbeReadyAsync(Process proc, int port)
    {
        var client = _httpFactory.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(ReadyTimeoutSec);
        var url = $"http://127.0.0.1:{port}/health";
        while (!_lifetimeCts.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (SafeHasExited(proc)) return; // MonitorAsync handles the crash/restart
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                cts.CancelAfter(2000);
                using var resp = await client.GetAsync(url, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _readyAt = DateTime.UtcNow;
                    _consecutiveFailures = 0;
                    SetState(EmbeddedState.Ready, null);
                    _logger.LogInformation("Embedded llama-server ready on port {Port}", port);
                    return;
                }
            }
            catch { /* not up yet */ }
            try { await Task.Delay(500, _lifetimeCts.Token); } catch { return; }
        }
        if (_state == EmbeddedState.Starting && !SafeHasExited(proc))
            SetState(EmbeddedState.Failed, "Timed out waiting for llama-server to become healthy.");
    }

    private async Task MonitorAsync(Process proc)
    {
        try { await proc.WaitForExitAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) { return; }

        if (_lifetimeCts.IsCancellationRequested || _userStopRequested) return;
        if (!ReferenceEquals(proc, _process)) return; // superseded by a switch/restart

        var exitCode = SafeExitCode(proc);
        await HandleCrashAsync(exitCode);
    }

    private async Task HandleCrashAsync(int exitCode)
    {
        // Treat a long-lived process that just died as a fresh failure streak.
        if (_readyAt is { } r && (DateTime.UtcNow - r).TotalSeconds > 60) _consecutiveFailures = 0;
        _consecutiveFailures++;

        var snippet = LastStderrSnippet();
        var baseErr = $"llama-server exited (code {exitCode}). {snippet}";

        if (_consecutiveFailures > MaxRestarts)
        {
            SetState(EmbeddedState.Failed, baseErr + " — giving up after repeated failures.");
            _logger.LogError("Embedded llama-server crash-looped; parking Failed. {Err}", baseErr);
            return;
        }

        var delayMs = Math.Min(30_000, 1000 * (int)Math.Pow(2, _consecutiveFailures - 1));
        SetState(EmbeddedState.Starting, baseErr + $" Restarting in {delayMs / 1000}s…");
        _logger.LogWarning("Embedded llama-server crashed; restart {N}/{Max} in {Ms}ms. {Err}",
            _consecutiveFailures, MaxRestarts, delayMs, baseErr);

        try { await Task.Delay(delayMs, _lifetimeCts.Token); } catch { return; }

        await _procLock.WaitAsync(_lifetimeCts.Token);
        try
        {
            if (_lifetimeCts.IsCancellationRequested || IsProcAlive()) return;
            var cfg = await ReadConfigAsync(_lifetimeCts.Token);
            if (cfg.Provider == "embedded" && cfg.ModelFile is not null)
                LaunchFromConfigLocked(cfg);
            else
                SetState(EmbeddedState.Stopped, null);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally { _procLock.Release(); }
    }

    // ── Download ─────────────────────────────────────────────────────────────────

    /// <summary>Validate + start a model download. Throws
    /// <see cref="InvalidOperationException"/> when one is already running
    /// (endpoint maps to 409) and <see cref="ArgumentException"/> on bad input
    /// (endpoint maps to 400). The actual transfer runs on a background task.</summary>
    public Task StartDownloadAsync(string modelId, string? customRepo, string? customFile, CancellationToken ct)
    {
        string repo, file;
        LlamaCatalogEntry? entry = null;

        if (string.Equals(modelId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            repo = (customRepo ?? "").Trim();
            file = (customFile ?? "").Trim();
            if (!RepoRx.IsMatch(repo))
                throw new ArgumentException("Invalid Hugging Face repo. Expected 'Owner/Name'.");
            if (!FileRx.IsMatch(file))
                throw new ArgumentException("Invalid file name. Expected a single '*.gguf' file (no paths).");
        }
        else
        {
            entry = LlamaModelCatalog.ById(modelId)
                ?? throw new ArgumentException($"Unknown model id '{modelId}'.");
            repo = entry.HfRepo;
            file = entry.FileName;
        }

        // Path-traversal defence (also guards the curated entries).
        if (_modelPaths.ResolveModelFile(file) is null)
            throw new ArgumentException("Resolved model path escapes the models directory.");

        if (!_downloadLock.Wait(0))
            throw new InvalidOperationException("A model download is already in progress.");

        // Disk pre-check (best effort — only when we know the size).
        var approx = entry?.ApproxBytes ?? 0;
        if (approx > 0 && _modelPaths.FreeBytes() is var free and > 0 && free < approx + approx / 10)
        {
            _downloadLock.Release();
            throw new InvalidOperationException("Not enough free disk space for this model.");
        }

        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _currentDownload = new DownloadProgress(modelId, file, 0, approx > 0 ? approx : null, 0, "downloading", null);
        _ = Task.Run(() => DownloadCoreAsync(modelId, repo, file, _downloadCts.Token));
        return Task.CompletedTask;
    }

    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { /* ignore */ }
    }

    private async Task DownloadCoreAsync(string modelId, string repo, string file, CancellationToken ct)
    {
        var dest = _modelPaths.ResolveModelFile(file)!;
        var part = dest + ".part";
        try
        {
            var client = _httpFactory.CreateClient("llama-hf");
            // Initial host is hardcoded to huggingface.co and repo/file are
            // regex-validated, so the user can't point this at an arbitrary
            // host (anti-SSRF). HF's own 302 → CDN redirect is followed by the
            // handler; acceptable for a single-user self-hosted app.
            var url = $"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(file)}?download=true";
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            if (total is > (long)MaxModelGiB * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Reported file size exceeds the safety cap.");

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs  = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long got = 0;
                var lastTick = Environment.TickCount64;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    got += read;
                    if (Environment.TickCount64 - lastTick >= 250)
                    {
                        var pct = total is > 0 ? 100.0 * got / total.Value : 0;
                        _currentDownload = new DownloadProgress(modelId, file, got, total, pct, "downloading", null);
                        lastTick = Environment.TickCount64;
                    }
                }
            }

            File.Move(part, dest, overwrite: true);
            var size = new FileInfo(dest).Length;
            _currentDownload = new DownloadProgress(modelId, file, size, total ?? size, 100, "done", null);
            _logger.LogInformation("Downloaded embedded model {File} ({Bytes} bytes)", file, size);

            await SaveSelectedModelAsync(modelId, file,
                repo: string.Equals(modelId, "custom", StringComparison.OrdinalIgnoreCase) ? repo : null, ct);
            await RestartAsync(_lifetimeCts.Token); // auto-load the freshly downloaded model
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            var d = _currentDownload;
            _currentDownload = new DownloadProgress(modelId, file, d?.BytesReceived ?? 0, d?.TotalBytes, d?.Percent ?? 0, "cancelled", "Download cancelled.");
        }
        catch (Exception ex)
        {
            TryDelete(part);
            var d = _currentDownload;
            _currentDownload = new DownloadProgress(modelId, file, d?.BytesReceived ?? 0, d?.TotalBytes, d?.Percent ?? 0, "error", ex.Message);
            _logger.LogWarning(ex, "Embedded model download failed for {Repo}/{File}", repo, file);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _downloadLock.Release();
        }
    }

    // ── GPU decision ─────────────────────────────────────────────────────────────

    private void DecideGpu()
    {
        _gpuMode = "cpu";
        _gpuActive = false;

        if (Environment.GetEnvironmentVariable("ANIMARR_LLM_VULKAN") != "1")
            return;

        if (!File.Exists(VulkanBinary))
        {
            _error = "ANIMARR_LLM_VULKAN set but the Vulkan llama-server binary is missing — running on CPU.";
            _logger.LogWarning("{Msg}", _error);
            return;
        }

        var hasRenderNode = false;
        try { hasRenderNode = Directory.Exists("/dev/dri") && Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*").Any(); }
        catch { /* ignore */ }
        if (!hasRenderNode)
        {
            _error = "ANIMARR_LLM_VULKAN set but no /dev/dri render node — pass `--device /dev/dri`. Running on CPU.";
            _logger.LogWarning("{Msg}", _error);
            return;
        }

        // Confirm the Vulkan binary can load its shared libs (libvulkan installed
        // by the entrypoint). If not, fall back to CPU.
        if (!RunProbe(VulkanBinary, "--version", 2500))
        {
            _error = "Vulkan requested but its userspace libraries are unavailable — running on CPU.";
            _logger.LogWarning("{Msg}", _error);
            return;
        }

        _gpuMode = "vulkan";
        _gpuActive = true;
        _logger.LogInformation("Embedded llama: Vulkan GPU offload enabled.");
    }

    private static bool RunProbe(string file, string arg, int timeoutMs)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
                },
            };
            SetLibraryPath(p.StartInfo, file);
            p.StartInfo.ArgumentList.Add(arg);
            if (!p.Start()) return false;
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Config + persistence ─────────────────────────────────────────────────────

    private sealed record EmbeddedConfig(string Provider, string? ModelId, string? ModelFile, int Port, int Ctx, int NglConfig);

    private async Task<EmbeddedConfig> ReadConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        var provider  = await cfg.GetAsync(AppConfigKeys.LlmProvider, "compatible", ct) ?? "compatible";
        var modelId   = await cfg.GetAsync(AppConfigKeys.LlmEmbeddedModelId, ct);
        var modelFile = await cfg.GetAsync(AppConfigKeys.LlmEmbeddedModelFile, ct);
        var port      = await cfg.GetAsync<int>(AppConfigKeys.LlmEmbeddedPort, DefaultPort, ct);
        var ctx       = await cfg.GetAsync<int>(AppConfigKeys.LlmEmbeddedContextSize, 4096, ct);
        var ngl       = await cfg.GetAsync<int>(AppConfigKeys.LlmEmbeddedGpuLayers, 999, ct);
        return new EmbeddedConfig(provider, modelId,
            string.IsNullOrWhiteSpace(modelFile) ? null : modelFile, port, ctx, ngl);
    }

    private async Task SaveSelectedModelAsync(string modelId, string fileName, string? repo, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        await cfg.SetAsync(AppConfigKeys.LlmEmbeddedModelId, modelId, ct);
        await cfg.SetAsync(AppConfigKeys.LlmEmbeddedModelFile, fileName, ct);
        if (repo is not null) await cfg.SetAsync(AppConfigKeys.LlmEmbeddedHfRepo, repo, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string CpuBinary    => Environment.GetEnvironmentVariable("ANIMARR_LLAMA_CPU_BIN")    ?? "/opt/llama/cpu/llama-server";
    private static string VulkanBinary => Environment.GetEnvironmentVariable("ANIMARR_LLAMA_VULKAN_BIN") ?? "/opt/llama/vulkan/llama-server";

    private void SetState(EmbeddedState state, string? error)
    {
        _state = state;
        _error = error;
        if (state == EmbeddedState.Stopped || state == EmbeddedState.Failed) _readyAt = _readyAt is null ? null : _readyAt;
    }

    private bool IsProcAlive()
    {
        try { return _process is { } p && !p.HasExited; }
        catch { return false; }
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }
    private static int  SafeExitCode(Process p)  { try { return p.ExitCode; } catch { return -1; } }

    private void CaptureStderr(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_stderrLock)
        {
            _stderrRing.Enqueue(line);
            while (_stderrRing.Count > 30) _stderrRing.Dequeue();
        }
    }

    private string LastStderrSnippet()
    {
        lock (_stderrLock)
        {
            if (_stderrRing.Count == 0) return "";
            var tail = _stderrRing.TakeLast(4);
            return "Last output: " + string.Join(" | ", tail);
        }
    }

    private static int PickFreePort(int preferred)
    {
        for (int p = preferred; p < preferred + 20; p++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); l.Stop(); return p; }
            catch { /* taken */ }
        }
        var any = new TcpListener(IPAddress.Loopback, 0);
        any.Start();
        var port = ((IPEndPoint)any.LocalEndpoint).Port;
        any.Stop();
        return port;
    }

    private static void SetLibraryPath(ProcessStartInfo psi, string binary)
    {
        // The official llama.cpp binary is a thin launcher that loads its
        // sibling .so files (libllama, libggml-*, …); we copy the whole dir in
        // the Dockerfile, so point the loader at it regardless of RPATH.
        var dir = Path.GetDirectoryName(binary);
        if (string.IsNullOrEmpty(dir)) return;
        var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? dir : $"{dir}:{existing}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
