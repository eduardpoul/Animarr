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

        return app;
    }
}
