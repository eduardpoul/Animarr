using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// DLNA / UPnP MediaServer surface — device description + ContentDirectory /
/// ConnectionManager SCPDs and their SOAP control + event-subscription stubs.
/// SSDP advertisement itself lives in <see cref="DlnaService"/> (a hosted
/// service). All AllowAnonymous: LAN TVs reach these without a cookie.
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

    app.MapGet("/dlna/desc.xml", (HttpContext http, DlnaService dlna) =>
    {
        return Results.Content(dlna.GetDeviceDescriptionXml(DlnaBaseUrl(http)), "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    app.MapGet("/dlna/cd.xml", (DlnaService dlna) =>
        Results.Content(dlna.GetContentDirectoryScpd(), "text/xml; charset=\"utf-8\"")).AllowAnonymous();

    app.MapGet("/dlna/cm.xml", (DlnaService dlna) =>
        Results.Content(dlna.GetConnectionManagerScpd(), "text/xml; charset=\"utf-8\"")).AllowAnonymous();

    app.MapPost("/dlna/cd/control", async (HttpContext http, DlnaService dlna) =>
    {
        using var sr = new StreamReader(http.Request.Body);
        var body = await sr.ReadToEndAsync();
        var resp = await dlna.HandleContentDirectoryAsync(body, DlnaBaseUrl(http));
        return Results.Content(resp, "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    app.MapPost("/dlna/cm/control", async (HttpContext http, DlnaService dlna) =>
    {
        using var sr = new StreamReader(http.Request.Body);
        var body = await sr.ReadToEndAsync();
        var resp = dlna.HandleConnectionManager(body);
        return Results.Content(resp, "text/xml; charset=\"utf-8\"");
    }).AllowAnonymous();

    // Event subscription stubs — many TVs SUBSCRIBE just to confirm the service
    // is alive. We return a fake SID + 200 OK; we don't actually emit events.
    app.MapMethods("/dlna/cd/event", new[] { "SUBSCRIBE", "UNSUBSCRIBE" }, (HttpContext http) =>
    {
        http.Response.Headers["SID"] = "uuid:" + Guid.NewGuid().ToString();
        http.Response.Headers["TIMEOUT"] = "Second-1800";
        return Results.Ok();
    }).AllowAnonymous();
    app.MapMethods("/dlna/cm/event", new[] { "SUBSCRIBE", "UNSUBSCRIBE" }, (HttpContext http) =>
    {
        http.Response.Headers["SID"] = "uuid:" + Guid.NewGuid().ToString();
        http.Response.Headers["TIMEOUT"] = "Second-1800";
        return Results.Ok();
    }).AllowAnonymous();

        return app;
    }
}
