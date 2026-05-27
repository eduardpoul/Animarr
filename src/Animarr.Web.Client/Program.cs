using Animarr.Shared;
using Animarr.UI;
using Animarr.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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
// real strings instead of keys on initial paint.
var loc = host.Services.GetRequiredService<LocalizationService>();
await loc.LoadAsync("en");

await host.RunAsync();
