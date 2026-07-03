using Animarr.Shared;
using Animarr.UI;
using Animarr.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Animarr.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Shared mutable holder for the active server URL — kept symmetric with the
// MAUI host so ServerRegistryState can swap servers without touching
// HttpClient.BaseAddress (which can't be changed after the first request).
// In the WASM case the provider is essentially pinned to the page origin —
// the bundle ITSELF was served from this host, so cross-origin switches go
// through a hard `forceLoad` anyway (ProfilePanel.OnSwitchServer).
builder.Services.AddScoped<ServerAddressProvider>(_ =>
    new ServerAddressProvider { Current = new Uri(builder.HostEnvironment.BaseAddress) });

// Single HttpClient — BaseAddress = the page origin (kept as a placeholder
// for HttpClient.PrepareRequestMessage's relative-URI check). The handler
// chain rewrites the authority of each request to whatever
// ServerAddressProvider.Current points at — same pattern as MAUI.
builder.Services.AddScoped<HttpClient>(sp =>
{
    var addr = sp.GetRequiredService<ServerAddressProvider>();
    var pipeline = new ServerAddressHandler(addr)
    {
        InnerHandler = new HttpClientHandler(),
    };
    return new HttpClient(pipeline)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    };
});

// IAnimarrApiClient + client-side state services that Animarr.UI pages depend on.
builder.Services.AddAnimarrApiClient();
builder.Services.AddAnimarrUiState();
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<ToastService>();

var host = builder.Build();

// Load the active language pack before the first render so L["..."] returns
// real strings instead of keys on initial paint. Prefer the language cached in
// localStorage by the previous session (written by MainLayout / ProfilePanel):
// a non-English user's first paint is then already localized instead of
// flashing English until /api/me/preferences resolves. Falls back to "en" on
// an empty cache or any interop hiccup; LoadAsync itself falls back to en.json
// for a missing pack.
var loc = host.Services.GetRequiredService<LocalizationService>();
var bootLang = "en";
try
{
    if (host.Services.GetRequiredService<IJSRuntime>() is IJSInProcessRuntime jsSync)
    {
        var cached = jsSync.Invoke<string?>("localStorage.getItem", "animarr-lang");
        if (!string.IsNullOrWhiteSpace(cached)) bootLang = cached;
    }
}
catch { /* keep "en" */ }
await loc.LoadAsync(bootLang);

await host.RunAsync();
