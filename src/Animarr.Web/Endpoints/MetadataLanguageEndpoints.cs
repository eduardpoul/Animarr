using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;
// AppConfigKeys is taken from Animarr.Shared (same constant values as the server-side
// mirror) — importing Animarr.Web.Data.Models too would make the name ambiguous.

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for the metadata-language setting and its library re-localisation pass.
/// Backs the "Metadata language" control in Settings → Metadata.
/// </summary>
internal static class MetadataLanguageEndpoints
{
    // Languages offered for metadata — kept in lockstep with the UI language list
    // (LocalizationService.SupportedLanguages).
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "en", "ru", "uk", "de", "es" };

    public static IEndpointRouteBuilder MapMetadataLanguageEndpoints(this IEndpointRouteBuilder app)
    {
        // Current language + re-localisation progress (polled by the Settings UI).
        app.MapGet(ApiRoutes.MetadataLanguage, async (
            IAppConfigService config,
            MetadataLanguageService svc,
            CancellationToken ct) =>
        {
            var lang = await config.GetAsync(AppConfigKeys.MetadataLanguage, ct) ?? "en";
            var (_, total, done, phase, error) = svc.Status;
            return Results.Ok(new MetadataLanguageStatusDto(lang, total, done, phase, error));
        });

        // Change the language and start re-fetching the library's texts/posters.
        app.MapPut(ApiRoutes.MetadataLanguage, async (
            MetadataLanguageRequest req,
            MetadataLanguageService svc,
            CancellationToken ct) =>
        {
            var lang = req.Language?.Trim().ToLowerInvariant() ?? "";
            if (!Allowed.Contains(lang))
                return Results.BadRequest(new
                {
                    error = $"Unsupported metadata language '{req.Language}'. Use one of: {string.Join(", ", Allowed)}."
                });

            await svc.StartAsync(lang, ct);
            return Results.Accepted();
        });

        return app;
    }
}
