using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for the AppConfig key/value store + the hardware probe.
/// Backs the Settings page.
/// </summary>
internal static class AppConfigEndpoints
{
    public static IEndpointRouteBuilder MapAppConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.AppConfig, async (
            IAppConfigService config,
            CancellationToken ct) =>
        {
            var all = await config.GetAllAsync(prefix: null, ct);
            return Results.Ok(all
                .Select(kv => new AppConfigEntryDto(kv.Key, kv.Value))
                .ToArray());
        });

        app.MapGet(ApiRoutes.AppConfigByKey, async (
            string key,
            IAppConfigService config,
            CancellationToken ct) =>
        {
            var value = await config.GetAsync(key, ct);
            return value is null
                ? Results.NotFound()
                : Results.Ok(new AppConfigEntryDto(key, value));
        });

        app.MapPut(ApiRoutes.AppConfigByKey, async (
            string key,
            AppConfigEntryDto entry,
            IAppConfigService config,
            CancellationToken ct) =>
        {
            // We use the key from the URL — body is just for the value payload.
            await config.SetAsync(key, entry.Value, ct);
            return Results.NoContent();
        });

        app.MapGet(ApiRoutes.HardwareInfo, (HardwareInfoService hw, bool? rescan) =>
        {
            if (rescan == true) hw.Rescan();
            return Results.Ok(hw.Current.ToDto());
        });

        return app;
    }
}
