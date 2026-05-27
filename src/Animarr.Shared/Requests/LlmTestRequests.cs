namespace Animarr.Shared.Requests;

/// <summary>
/// Result of <c>POST /api/llm/test</c> — a small ping against the configured
/// LLM endpoint that returns enough detail for the AI tab to draw a
/// success / failure card without scraping logs.
/// </summary>
/// <param name="Ok">Whether the LLM responded with a non-empty completion.</param>
/// <param name="LatencyMs">Wall time from request-out to response-in. Includes
/// network + queue + token generation, so a slow first model load (Ollama
/// cold start, etc.) shows up here.</param>
/// <param name="Sample">The first 80 chars of the model's reply. Useful so the
/// admin can tell "this is the right model" from "this is a fallback" at a
/// glance.</param>
/// <param name="Error">Human-readable error message when <see cref="Ok"/> is
/// false. Sourced from the underlying HTTP / SDK exception's Message — never
/// the full stack trace, that lives in the server logs.</param>
public sealed record LlmTestResponse(
    bool    Ok,
    int     LatencyMs,
    string? Sample,
    string? Error);
