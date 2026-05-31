using System.Diagnostics;
using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Animarr.Web.Endpoints;

/// <summary>
/// LLM diagnostic endpoints — currently a single ping/test that the AI
/// settings tab uses to verify the configured provider before queuing real
/// identifications. The implementation calls <see cref="ILlmService.GetCompletionAsync"/>
/// with a tiny prompt and times the round trip.
///
/// Restricted to admins (manageSystem). The endpoint reads saved AppConfig —
/// the admin must hit Save in the UI before the test reflects unsaved
/// changes.
/// </summary>
internal static class LlmEndpoints
{
    /// <summary>Tiny prompt that should produce a deterministic short reply
    /// across all OpenAI-compatible models. Picked to avoid system-prompt
    /// quirks and to keep the response small enough that even slow models
    /// respond in &lt;5s.</summary>
    private const string PingPrompt =
        "Reply with exactly one word: pong. No punctuation, no explanation.";

    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.LlmTest, async (
            [FromServices] ILlmService llm,
            [FromServices] ILogger<MicrosoftAiLlmService> logger,
            CancellationToken ct) =>
        {
            // Cap the request so a wedged Ollama / unreachable upstream can't
            // hold the connection open forever. 15s comfortably covers cold
            // Ollama model loads (~5–8s for small qwen) but stays UI-friendly.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(15));

            var sw = Stopwatch.StartNew();
            try
            {
                var sample = await llm.GetCompletionAsync(PingPrompt, linked.Token);
                sw.Stop();

                if (string.IsNullOrWhiteSpace(sample))
                {
                    return Results.Json(new LlmTestResponse(
                        Ok: false,
                        LatencyMs: (int)sw.ElapsedMilliseconds,
                        Sample: null,
                        Error: "LLM returned an empty response. Check provider, base URL, and model name."));
                }

                // Trim the sample so the UI card stays compact — admins want to
                // see "is the right model talking back" not the full completion.
                var trimmed = sample.Length > 80 ? sample.Substring(0, 80) + "…" : sample;
                return Results.Json(new LlmTestResponse(
                    Ok: true,
                    LatencyMs: (int)sw.ElapsedMilliseconds,
                    Sample: trimmed,
                    Error: null));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop();
                return Results.Json(new LlmTestResponse(
                    Ok: false,
                    LatencyMs: (int)sw.ElapsedMilliseconds,
                    Sample: null,
                    Error: "Timed out after 15s. Check that the LLM server is running and reachable."));
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Surface the immediate exception message — the SDK wraps
                // connection refused / 401 / 404 / parse errors here. Full
                // stack stays in server logs.
                logger.LogWarning(ex, "LLM test ping failed");
                return Results.Json(new LlmTestResponse(
                    Ok: false,
                    LatencyMs: (int)sw.ElapsedMilliseconds,
                    Sample: null,
                    Error: ex.Message));
            }
        })
        // Was .RequireAuthorization("manageSystem") — NO such policy is
        // registered (Program.cs registers ViewContent / UploadContent /
        // SystemSettings / ManageUsers). An unknown policy name makes ASP.NET
        // throw "policy not found" BEFORE the handler runs → instant 500
        // ("Failed after 0 ms"). Use the real systemSettings policy.
        .RequireAuthorization(Animarr.Web.Services.Auth.AuthConstants.Policies.SystemSettings)
        .WithName("LlmTestPing");

        // ─── Embedded llama.cpp provider ──────────────────────────────────────
        const string adminPolicy = Animarr.Web.Services.Auth.AuthConstants.Policies.SystemSettings;

        // Curated catalog + installed files + free disk for the AI tab.
        app.MapGet(ApiRoutes.LlmEmbeddedCatalog, (
            [FromServices] EmbeddedLlamaService embedded,
            [FromServices] ModelPaths paths) =>
        {
            var installed = paths.ListInstalled();
            var bySize = installed.ToDictionary(i => i.FileName, i => i.Bytes, StringComparer.OrdinalIgnoreCase);
            var activeFile = embedded.Status.ModelFile;

            var curated = LlamaModelCatalog.Entries.Select(e => new LlamaCatalogEntryDto(
                e.Id, e.DisplayName, e.HfRepo, e.FileName, e.ApproxBytes, e.RecommendedRamMb,
                e.CpuFriendly, e.GpuRecommended, e.Notes,
                Installed: bySize.ContainsKey(e.FileName),
                InstalledBytes: bySize.TryGetValue(e.FileName, out var b) ? b : null)).ToArray();

            var installedDtos = installed.Select(i => new InstalledModelDto(
                i.FileName, i.Bytes,
                IsActive: string.Equals(i.FileName, activeFile, StringComparison.OrdinalIgnoreCase))).ToArray();

            return Results.Json(new LlamaCatalogResponse(curated, installedDtos, paths.FreeBytes()));
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedCatalog");

        // Start a download (curated id, or "custom" + repo/file). 400 bad input, 409 busy.
        app.MapPost(ApiRoutes.LlmEmbeddedDownload, async (
            [FromBody] StartDownloadRequest req,
            [FromServices] EmbeddedLlamaService embedded,
            CancellationToken ct) =>
        {
            try
            {
                await embedded.StartDownloadAsync(req.ModelId, req.CustomRepo, req.CustomFile, ct);
                return Results.Accepted();
            }
            catch (ArgumentException ex)        { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedDownloadStart");

        // In-flight download progress (Phase="idle" when nothing is downloading).
        app.MapGet(ApiRoutes.LlmEmbeddedDownload, ([FromServices] EmbeddedLlamaService embedded) =>
        {
            var d = embedded.CurrentDownload;
            return Results.Json(new DownloadProgressDto(
                d.ModelId, d.FileName, d.BytesReceived, d.TotalBytes, d.Percent, d.Phase, d.Error));
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedDownloadStatus");

        // Cancel the in-flight download.
        app.MapDelete(ApiRoutes.LlmEmbeddedDownload, ([FromServices] EmbeddedLlamaService embedded) =>
        {
            embedded.CancelDownload();
            return Results.NoContent();
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedDownloadCancel");

        // Installed model files.
        app.MapGet(ApiRoutes.LlmEmbeddedModels, (
            [FromServices] EmbeddedLlamaService embedded,
            [FromServices] ModelPaths paths) =>
        {
            var active = embedded.Status.ModelFile;
            var list = paths.ListInstalled().Select(i => new InstalledModelDto(
                i.FileName, i.Bytes,
                string.Equals(i.FileName, active, StringComparison.OrdinalIgnoreCase))).ToArray();
            return Results.Json(list);
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedModels");

        // Delete an installed model file. Refuses the active one (switch/stop first).
        app.MapDelete(ApiRoutes.LlmEmbeddedModelByFile, (
            string file,
            [FromServices] EmbeddedLlamaService embedded,
            [FromServices] ModelPaths paths) =>
        {
            var full = paths.ResolveModelFile(file);
            if (full is null) return Results.BadRequest(new { error = "Invalid file name." });
            if (string.Equals(file, embedded.Status.ModelFile, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "Cannot delete the active model. Switch model or restart first." });
            try
            {
                if (File.Exists(full)) File.Delete(full);
                return Results.NoContent();
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        })
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedModelDelete");

        // Embedded runtime status (state/model/gpu/port).
        app.MapGet(ApiRoutes.LlmEmbeddedStatus, ([FromServices] EmbeddedLlamaService embedded) =>
            Results.Json(ToStatusDto(embedded.Status)))
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedStatus");

        // Restart the embedded child (e.g. after changing context/GPU settings).
        app.MapPost(ApiRoutes.LlmEmbeddedRestart, async (
            [FromServices] EmbeddedLlamaService embedded,
            CancellationToken ct) =>
            Results.Json(ToStatusDto(await embedded.RestartAsync(ct))))
        .RequireAuthorization(adminPolicy)
        .WithName("LlmEmbeddedRestart");

        return app;
    }

    private static EmbeddedStatusDto ToStatusDto(EmbeddedRuntimeStatus s)
        => new(s.State.ToString(), s.ModelId, s.ModelFile, s.Port, s.GpuActive, s.GpuMode, s.Error, s.ReadyAt);
}
