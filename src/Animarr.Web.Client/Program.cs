using Animarr.Shared;
using Animarr.UI;
using Animarr.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Animarr.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Single HttpClient — BaseAddress = the page origin, so the WASM client
// talks to whichever server is serving the static files. In production
// (Animarr.Web hosts both the API and the WASM blob) this is the same
// host. In dev mode against a remote tower.one, it's that origin.
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

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
