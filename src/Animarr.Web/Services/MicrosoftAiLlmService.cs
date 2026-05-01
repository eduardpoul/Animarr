using System.Text.Json;
using System.Text.Json.Serialization;
using Animarr.Web.Data.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace Animarr.Web.Services;

/// <summary>
/// LLM service backed by Microsoft.Extensions.AI.
/// Supports any OpenAI-compatible endpoint: OpenAI, Ollama, LM Studio, Groq, Together, etc.
/// Provider is selected at runtime from AppConfig — no restart needed when switching.
/// </summary>
public class MicrosoftAiLlmService(
    IAppConfigService appConfig,
    ILogger<MicrosoftAiLlmService> logger) : ILlmService
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── ILlmService ───────────────────────────────────────────────────────────

    public async Task<LlmIdentifyResult?> IdentifyFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

        var prompt =
            "You are a media library assistant. Identify the following media folder name and extract structured information.\n\n" +
            "Folder name: \"" + folderName + "\"\n\n" +
            "Respond ONLY with valid JSON matching this exact schema:\n" +
            "{\n" +
            "  \"title\": \"English title of the media\",\n" +
            "  \"original_title\": \"Original title if different from English, or null\",\n" +
            "  \"year\": integer release year or null,\n" +
            "  \"type\": \"anime\" | \"series\" | \"movie\" | \"unknown\",\n" +
            "  \"season\": integer season number if detectable or null,\n" +
            "  \"confidence\": float from 0.0 to 1.0,\n" +
            "  \"suggested_regex\": \"A .NET regex pattern to match episode files in this folder, or null\"\n" +
            "}\n\n" +
            "Rules:\n" +
            "- \"type\" is \"anime\" for Japanese animation, \"series\" for live-action TV, \"movie\" for films\n" +
            "- \"confidence\" reflects how sure you are about the identification (1.0 = certain)\n" +
            "- For \"suggested_regex\", include named groups: (?<episode>...) and optionally (?<season>...)\n" +
            "- Return ONLY the JSON object, no extra text";

        var raw = await CallAsync(prompt, ct);
        if (raw is null) return null;

        try
        {
            var result = JsonSerializer.Deserialize<LlmIdentifyRaw>(raw, _json);
            if (result is null || string.IsNullOrWhiteSpace(result.Title)) return null;
            return new LlmIdentifyResult
            {
                Title          = result.Title,
                OriginalTitle  = result.OriginalTitle,
                Year           = result.Year,
                Type           = result.Type ?? "unknown",
                Season         = result.Season,
                Confidence     = result.Confidence,
                SuggestedRegex = result.SuggestedRegex,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM identify response: {Raw}", raw);
            return null;
        }
    }

    public async Task<LlmRegexResult?> SuggestRegexAsync(string fileName, CancellationToken ct = default)
    {
        var prompt =
            "You are a media library assistant. Suggest a .NET regex pattern to match the following filename.\n\n" +
            "Filename: \"" + fileName + "\"\n\n" +
            "Respond ONLY with valid JSON:\n" +
            "{\n" +
            "  \"pattern\": \".NET regex with named group (?<episode>\\\\d+) and optionally (?<season>\\\\d+)\",\n" +
            "  \"explanation\": \"Brief explanation of the pattern\",\n" +
            "  \"confidence\": float from 0.0 to 1.0\n" +
            "}\n\n" +
            "Return ONLY the JSON object, no extra text.";

        var raw = await CallAsync(prompt, ct);
        if (raw is null) return null;

        try
        {
            return JsonSerializer.Deserialize<LlmRegexResult>(raw, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM regex response: {Raw}", raw);
            return null;
        }
    }

    public async Task<int?> SelectCandidateAsync(string folderName, List<LlmCandidateItem> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return 0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a media library assistant. Select the best matching search result for the given folder name.");
        sb.AppendLine();
        sb.AppendLine($"Folder name: \"{folderName}\"");
        sb.AppendLine();
        sb.AppendLine("Candidates:");
        foreach (var c in candidates)
        {
            sb.AppendLine($"[{c.Index}] Source={c.Source} | Title=\"{c.Title}\" | Year={c.Year?.ToString() ?? "?"} | Type={c.Type ?? "?"}");
            if (!string.IsNullOrEmpty(c.Overview))
                sb.AppendLine($"    {c.Overview[..Math.Min(120, c.Overview.Length)]}...");
        }
        sb.AppendLine();
        sb.AppendLine("Respond ONLY with JSON: {\"selected\": <index>}");
        sb.AppendLine("Choose the index of the best match. Consider title similarity, year, and type.");

        var raw = await CallAsync(sb.ToString(), ct);
        if (raw is null) return null;

        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(raw, _json);
            if (el.TryGetProperty("selected", out var sel) && sel.TryGetInt32(out var idx))
                return idx >= 0 && idx < candidates.Count ? idx : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM candidate selection: {Raw}", raw);
        }

        return null;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (client, _) = await CreateClientAsync(ct);
            if (client is null) return false;

            // Send a minimal completion to test connectivity
            var result = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 8 },
                ct);
            return result?.Text is not null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "LLM availability check failed");
            return false;
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task<string?> CallAsync(string userPrompt, CancellationToken ct)
    {
        try
        {
            var (client, _) = await CreateClientAsync(ct);
            if (client is null) return null;

            var chatOptions = new ChatOptions
            {
                Temperature     = 0.1f,
                MaxOutputTokens = 512,
                ResponseFormat  = ChatResponseFormat.Json,
            };

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, userPrompt)],
                chatOptions,
                ct);

            return response?.Text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM request failed");
            return null;
        }
    }

    /// <summary>
    /// Builds an IChatClient from current AppConfig settings.
    /// Returns (null, "") when LLM is disabled.
    /// Migrates legacy Ollama keys on first use if new keys are not yet set.
    /// </summary>
    private async Task<(IChatClient? client, string model)> CreateClientAsync(CancellationToken ct = default)
    {
        // ── Check enabled (try new key, fall back to legacy Ollama key) ────────
#pragma warning disable CS0618 // legacy key migration
        var enabled = await appConfig.GetAsync<bool>(AppConfigKeys.LlmEnabled, false, ct);
        if (!enabled)
        {
            // migrate: if old OllamaEnabled was true, honour it
            enabled = await appConfig.GetAsync<bool>(AppConfigKeys.OllamaEnabled, false, ct);
            if (!enabled) return (null, "");
        }
#pragma warning restore CS0618

        // ── Read new keys, falling back to legacy values ───────────────────────
        var provider = await appConfig.GetAsync(AppConfigKeys.LlmProvider, "compatible", ct) ?? "compatible";
        var apiKey   = await appConfig.GetAsync(AppConfigKeys.LlmApiKey, "", ct) ?? "";
        var model    = await appConfig.GetAsync(AppConfigKeys.LlmModel, "", ct) ?? "";

#pragma warning disable CS0618 // legacy key migration
        if (string.IsNullOrEmpty(model))
            model = await appConfig.GetAsync(AppConfigKeys.OllamaModel, "qwen2.5:1.5b", ct) ?? "qwen2.5:1.5b";
#pragma warning restore CS0618

        // ── Build OpenAI client ────────────────────────────────────────────────
        OpenAIClientOptions? options = null;

        if (provider == "compatible")
        {
            var baseUrl = await appConfig.GetAsync(AppConfigKeys.LlmBaseUrl, "", ct) ?? "";

#pragma warning disable CS0618 // legacy key migration
            if (string.IsNullOrEmpty(baseUrl))
                baseUrl = await appConfig.GetAsync(AppConfigKeys.OllamaUrl, "http://ollama:11434", ct) ?? "http://ollama:11434";
#pragma warning restore CS0618

            // Normalise: append /v1 if not already present
            if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.TrimEnd('/') + "/v1";

            options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        }

        // Local/unauthenticated services accept any non-empty key string
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey);

        var chatClient = new OpenAI.Chat.ChatClient(model: model, credential: credential, options: options)
            .AsIChatClient();
        return (chatClient, model);
    }

    // ── Private DTOs ─────────────────────────────────────────────────────────

    private sealed class LlmIdentifyRaw
    {
        public string Title { get; set; } = "";
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
        public int? Year { get; set; }
        public string? Type { get; set; }
        public int? Season { get; set; }
        public double Confidence { get; set; }
        [JsonPropertyName("suggested_regex")] public string? SuggestedRegex { get; set; }
    }
}
