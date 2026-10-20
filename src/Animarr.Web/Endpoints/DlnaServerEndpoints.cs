using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// DLNA / UPnP MediaServer surface — device description + ContentDirectory /
/// ConnectionManager SCPDs and their SOAP control + event-subscription stubs.
/// SSDP advertisement itself lives in <see cref="DlnaService"/> (a hosted
/// service).
///
/// These are the only library-facing routes that stay AllowAnonymous: a UPnP
/// control point (a smart TV's media browser) has no way to sign in. Instead:
///   • the whole surface answers 404 unless the admin switched the DLNA server
///     on in Settings (<c>dlna.server_enabled</c>, OFF by default);
///   • it answers only LAN clients — a direct connection from a private /
///     loopback / link-local address without reverse-proxy forwarding headers;
///   • every res URL it emits carries a file-bound DLNA link token, which
///     /api/file honours only under the same two conditions.
/// </summary>
internal static class DlnaServerEndpoints
{
    public static IEndpointRouteBuilder MapDlnaServerEndpoints(this IEndpointRouteBuilder app)
    {
    // ─── DLNA / UPnP MediaServer ─────────────────────────────────────────────
    // Serves the device description + ContentDirectory/ConnectionManager SCPDs
    // and routes SOAP control requests to DlnaService. SSDP advertisement is
    // handled inside DlnaService itself (background hosted service).
    //
    // baseUrl is constructed from the incoming request — TVs cache by LOCATION,
    // so as long as the same host:port is reachable from them, responses will
    // match. (We do NOT use the X-Forwarded-Host header path here because DLNA
    // clients on the LAN are talking to us directly.)
    static string DlnaBaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    // 404 unless DLNA networking is up AND the admin opted the MediaServer in,
    // and only for LAN clients (see the class summary).
    var server = app.MapGroup(string.Empty).AddEndpointFilter(async (ctx, next) =>
        await ctx.HttpContext.RequestServices.GetRequiredService<DlnaService>()
                .IsServerEnabledAsync(ctx.HttpContext.RequestAborted)
            && LanRequest.IsLan(ctx.HttpContext)
            ? await next(ctx)
            : Results.NotFound());

    server.MapGet("/dlna/desc.xml", (HttpContext http, DlnaService dlna) =>
    {
        return Results.Content(dlna.GetDeviceDescriptionXml(DlnaBaseUrl(http)), "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    server.MapGet("/dlna/cd.xml", (DlnaService dlna) =>
        Results.Content(dlna.GetContentDirectoryScpd(), "text/xml; charset=\"utf-8\"")).AllowAnonymous();

    server.MapGet("/dlna/cm.xml", (DlnaService dlna) =>
        Results.Content(dlna.GetConnectionManagerScpd(), "text/xml; charset=\"utf-8\"")).AllowAnonymous();

    server.MapPost("/dlna/cd/control", async (HttpContext http, DlnaService dlna) =>
    {
        using var sr = new StreamReader(http.Request.Body);
        var body = await sr.ReadToEndAsync();
        var resp = await dlna.HandleContentDirectoryAsync(body, DlnaBaseUrl(http));
        return Results.Content(resp, "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    server.MapPost("/dlna/cm/control", async (HttpContext http, DlnaService dlna) =>
    {
        using var sr = new StreamReader(http.Request.Body);
        var body = await sr.ReadToEndAsync();
        var resp = dlna.HandleConnectionManager(body);
        return Results.Content(resp, "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    // Event subscription stubs — many TVs SUBSCRIBE just to confirm the service
    // is alive. We return a fake SID + 200 OK; we don't actually emit events.
    server.MapMethods("/dlna/cd/event", new[] { "SUBSCRIBE", "UNSUBSCRIBE" }, (HttpContext http) =>
    {
        http.Response.Headers["SID"] = "uuid:" + Guid.NewGuid().ToString();
        http.Response.Headers["TIMEOUT"] = "Second-1800";
        return Results.Ok();
    }).AllowAnonymous();
    server.MapMethods("/dlna/cm/event", new[] { "SUBSCRIBE", "UNSUBSCRIBE" }, (HttpContext http) =>
    {
        http.Response.Headers["SID"] = "uuid:" + Guid.NewGuid().ToString();
        http.Response.Headers["TIMEOUT"] = "Second-1800";
        return Results.Ok();
    }).AllowAnonymous();

        return app;
    }
}
