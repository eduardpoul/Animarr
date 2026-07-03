using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Endpoints;
using Animarr.Web.Hubs;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Segments;
using Animarr.Web.Services.Trickplay;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// JSON for minimal-API request/response bodies. The shared HttpAnimarrApiClient
// (used by BOTH the MAUI app and the WASM web client) serialises enums AS NAMES
// via JsonStringEnumConverter — e.g. a metadata save sends {"mediaType":"Series"}.
// The minimal-API default (JsonSerializerDefaults.Web) has no string-enum
// converter, so it can only read NUMBERS; a string enum value throws a
// JsonException during model binding, which surfaces to the client as a bare
// 400 Bad Request ("Save failed: …, 400, Bad Request"). Registering the same
// converter here makes the server READ string enums (fixing the 400) and EMIT
// them too — both clients already decode names, so the contract stays in sync.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Bind AppSettings
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("AppSettings"));

// Built-in TMDB key for out-of-the-box metadata (see TmdbDefaults). Read once at
// startup; Metadata__TmdbApiKey env var overrides appsettings.json.
TmdbDefaults.BuiltInApiKey = builder.Configuration["Metadata:TmdbApiKey"];

// EF Core — SQLite
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=Animarr.db";

// Ensure the data directory exists (relevant for Docker volume)
var dbPath = connectionString.Replace("Data Source=", "").Trim();
var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(connectionString)
           .AddInterceptors(new SqliteWalInterceptor()));

// H-10: DataProtection so we can encrypt API keys in AppConfig.
// Keys are persisted under /app/data so they survive container restarts.
var dpKeysDir = string.IsNullOrEmpty(dbDir) ? "." : dbDir;
Directory.CreateDirectory(Path.Combine(dpKeysDir, "dp-keys"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dpKeysDir, "dp-keys")))
    .SetApplicationName("Animarr");
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<MediaCachePaths>();
// Central "may this disk path be served?" gate for every byte-serving endpoint.
builder.Services.AddSingleton<MediaPathValidator>();

// App services
builder.Services.AddScoped<SeedDataService>();
builder.Services.AddScoped<CategorySeedService>();
// Singleton OK — the classifier opens short-lived contexts via IDbContextFactory
// and resolves scoped services (IAppConfigService, ILlmService) through
// IServiceScopeFactory on each call.
builder.Services.AddSingleton<CategoryClassifierService>();
builder.Services.AddSingleton<IPatternMatchService, PatternMatchService>();
builder.Services.AddScoped<IAppConfigService, AppConfigService>();
builder.Services.AddSingleton<FolderWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FolderWatcherService>());
builder.Services.AddSingleton<TorrentEngineService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentEngineService>());

// HTTP clients
builder.Services.AddScoped<TmdbAuthHandler>();
builder.Services.AddHttpClient("tmdb", c =>
{
    c.DefaultRequestHeaders.Add("Accept", "application/json");
}).AddHttpMessageHandler<TmdbAuthHandler>();
builder.Services.AddScoped<MalAuthHandler>();
builder.Services.AddHttpClient("mal", c =>
{
    c.DefaultRequestHeaders.Add("Accept", "application/json");
}).AddHttpMessageHandler<MalAuthHandler>();
builder.Services.AddHttpClient("imdb_search", c =>
{
    c.BaseAddress = new Uri("https://api.imdbapi.dev");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
// AnimeThemes.moe — anime OP/ED theme audio (free, no auth). NOTE: its WAF
// returns 403 for requests with no/default User-Agent. .NET's HttpClient sends
// no UA by default, so an explicit one is REQUIRED here (curl only worked
// because it sends "curl/x.y"). Confirmed: blank UA → 403, any UA → 200.
builder.Services.AddHttpClient("animethemes", c =>
{
    c.BaseAddress = new Uri("https://api.animethemes.moe");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
    c.DefaultRequestHeaders.Add("User-Agent", "Animarr/1.0 (+https://github.com/eduardpoul/animarr)");
});
// AniList GraphQL — title→MAL-id bridge for theme lookup (free, no auth).
// AniList doesn't require a UA today, but set one defensively (same WAF risk).
builder.Services.AddHttpClient("anilist", c =>
{
    c.BaseAddress = new Uri("https://graphql.anilist.co");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
    c.DefaultRequestHeaders.Add("User-Agent", "Animarr/1.0 (+https://github.com/eduardpoul/animarr)");
});

// Metadata & LLM services
builder.Services.AddScoped<TmdbClient>();
builder.Services.AddScoped<MalClient>();
builder.Services.AddScoped<ImdbSearchClient>();
builder.Services.AddScoped<AnimeThemesClient>();
builder.Services.AddScoped<AniListClient>();
builder.Services.AddScoped<MetadataService>();
// Background "metadata language changed → re-fetch the library" pass + its progress.
builder.Services.AddSingleton<MetadataLanguageService>();
builder.Services.AddSingleton<IWatchStateService, WatchStateService>();
builder.Services.AddSingleton<HlsSessionService>();
builder.Services.AddSingleton<DlnaService>();
// Probes /dev/dri, vainfo, nvidia-smi, ffmpeg -hwaccels at startup so the
// Settings UI can show what's actually usable on this host.
builder.Services.AddSingleton<HardwareInfoService>();
builder.Services.AddScoped<MediaFileResolver>();
builder.Services.AddScoped<EpisodeLlmResolver>();
builder.Services.AddScoped<SeasonOffsetResolver>();
builder.Services.AddScoped<ExternalTrackService>();

// ─── Skip intro/credits — segment detection ───────────────────────────────
// AniSkip: crowd-sourced OP/ED timestamps by MAL id (free, no auth). Needs an
// explicit User-Agent for the same WAF reason as AnimeThemes/AniList above.
builder.Services.AddHttpClient(AniSkipClient.ClientName, c =>
{
    c.BaseAddress = new Uri("https://api.aniskip.com");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
    c.DefaultRequestHeaders.Add("User-Agent", "Animarr/1.0 (+https://github.com/eduardpoul/animarr)");
    // The API answers in ~0.3s when reachable; cap well below the 100s default so
    // an unreachable host (broken-MTU/DPI network) fails fast instead of hanging.
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<AniSkipClient>();
builder.Services.AddScoped<MalIdResolver>();   // title → MAL id via AniList (no key)
// Providers are resolved as an IEnumerable<ISegmentProvider> by the orchestrator,
// ordered by their cascade Order (cheapest first). Register-order independent.
builder.Services.AddScoped<ISegmentProvider, AniSkipProvider>();     // Order 0  — network, by MAL id
builder.Services.AddScoped<ISegmentProvider, ChapterProvider>();     // Order 10 — embedded chapters
builder.Services.AddScoped<ISegmentProvider, ChromaprintProvider>(); // Order 20 — audio fingerprint
builder.Services.AddScoped<ISegmentProvider, BlackFrameProvider>();  // Order 30 — video black-frame (opt-in)
builder.Services.AddScoped<SegmentDetectionService>();
// Heavy detection (chromaprint) over identified titles, one at a time.
builder.Services.AddHostedService<SegmentDetectionBackgroundService>();

// Trickplay — seek-preview sprite sheets, generated in the background one
// title at a time (yields to live HLS transcodes).
builder.Services.AddScoped<TrickplayService>();
builder.Services.AddHostedService<TrickplayBackgroundService>();

// Recommendations — heuristic "More like this" / "For you" rails with
// local-first scoring and TMDB backfill.
builder.Services.AddScoped<Animarr.Web.Services.Recs.RecsService>();

// Airing calendar — AniList/TMDB schedule refresh for ongoing titles.
builder.Services.AddHostedService<Animarr.Web.Services.Airing.AiringRefreshBackgroundService>();

// Franchise graphs — AniList relations BFS + watch-order rail.
builder.Services.AddScoped<Animarr.Web.Services.Franchise.FranchiseService>();
builder.Services.AddHostedService<Animarr.Web.Services.Franchise.FranchiseBackgroundService>();

// Filler/recap markers — Jikan (unofficial MAL) per-episode flags. Same WAF
// caution as the other public APIs: explicit User-Agent.
builder.Services.AddHttpClient(JikanClient.ClientName, c =>
{
    c.BaseAddress = new Uri("https://api.jikan.moe");
    c.DefaultRequestHeaders.Add("Accept", "application/json");
    c.DefaultRequestHeaders.Add("User-Agent", "Animarr/1.0 (+https://github.com/eduardpoul/animarr)");
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<JikanClient>();
builder.Services.AddHostedService<FillerRefreshBackgroundService>();

builder.Services.AddHostedService(sp => sp.GetRequiredService<DlnaService>());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DlnaCastService>();
builder.Services.AddScoped<ILlmService, MicrosoftAiLlmService>();

// Built-in ("embedded") llama.cpp provider: ModelPaths resolves the on-disk
// models dir; EmbeddedLlamaService supervises the in-container llama-server
// child + downloads GGUF weights. Singleton (one child per container) + hosted.
builder.Services.AddSingleton<ModelPaths>();
builder.Services.AddSingleton<EmbeddedLlamaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddedLlamaService>());
// Long-lived, UA-stamped client for Hugging Face GGUF downloads (streamed; we
// cancel via token, so no overall timeout).
builder.Services.AddHttpClient("llama-hf", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Animarr/1.0 (+https://github.com/eduardpoul/animarr)");
});

// Dual-registration: same instance available for DI into Blazor components
// (so the sidebar LLM status card + NeedsReview chip can subscribe to events)
// AND runs as a hosted service.
builder.Services.AddSingleton<IdentificationQueueProcessorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdentificationQueueProcessorService>());

// SignalR for the realtime hubs Animarr.UI pages consume. Hub paths
// (/hubs/torrents, /hubs/identification) are scoped under /hubs so they
// don't collide with /api/* or the WASM SPA fallback.
builder.Services.AddSignalR();

// v5 Phase 7 TV pairing: holds pending pair codes (5min TTL) so a phone can
// authorise a TV without the TV typing credentials. Single-server only.
builder.Services.AddMemoryCache();

// ─── v4 auth: cookie session + per-request user context ────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, UserContext>();
builder.Services.AddSingleton<AuthService>();

builder.Services
    .AddAuthentication(AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name        = AuthConstants.CookieName;
        options.Cookie.HttpOnly    = true;
        options.Cookie.SameSite    = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan      = TimeSpan.FromDays(14);
        options.SlidingExpiration   = true;
        // No login redirects — the SPA handles auth flow client-side. API
        // endpoints return 401 JSON so the client router can react.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddScoped<
    Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    PermissionAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    // Each policy is a PermissionRequirement carrying a selector against the
    // user's Role. The single PermissionAuthorizationHandler hydrates the
    // user through IUserContext (request-scoped cache) — first policy in a
    // request pays the DB hit, subsequent ones get the cached User.
    static Microsoft.AspNetCore.Authorization.AuthorizationPolicy PolicyFor(
        Func<Animarr.Web.Data.Models.Role, bool> selector) =>
        new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthConstants.CookieScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(selector))
            .Build();
    options.AddPolicy(AuthConstants.Policies.ViewContent,    PolicyFor(r => r.PermViewContent));
    options.AddPolicy(AuthConstants.Policies.UploadContent,  PolicyFor(r => r.PermUploadContent));
    options.AddPolicy(AuthConstants.Policies.SystemSettings, PolicyFor(r => r.PermSystemSettings));
    options.AddPolicy(AuthConstants.Policies.ManageUsers,    PolicyFor(r => r.PermManageUsers));

    // Default-deny: every endpoint that doesn't explicitly opt out with
    // .AllowAnonymous() (or carry its own policy) requires a signed-in user.
    // The media-byte surface (video/file/HLS segments/image/DLNA, pairing,
    // server-info) opts out deliberately — those are consumed by cookie-less
    // clients (DLNA renderers, mpv, the Android WebView proxy) and rely on
    // library-path validation instead.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthConstants.CookieScheme)
        .RequireAuthenticatedUser()
        .Build();
});

// Pairing endpoints are anonymous by design (the TV has no cookie yet), which
// makes the 6-digit code a brute-force target. A per-IP fixed window keeps a
// legitimate TV's 2s poll loop (30 req/min) comfortably inside the budget
// while capping a scanner to ~60 code guesses a minute — at 10⁶ codes and a
// 10-minute TTL that's a ~0.06% hit chance per pairing session.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Pair, httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window      = TimeSpan.FromMinutes(1),
                QueueLimit  = 0,
            }));
});
builder.Services.AddSingleton<TorrentHubBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentHubBroadcaster>());
builder.Services.AddSingleton<IdentificationHubBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdentificationHubBroadcaster>());

// v5 multi-server: publish _animarr._tcp on the LAN so the Discovery page
// can find this install without manual URL entry. Hosted service — soft-fails
// when multicast isn't available (Docker bridge, restricted NICs, etc.).
builder.Services.AddHostedService<MdnsPublisherService>();

var app = builder.Build();

// Apply EF Core migrations on startup. Skipped only when the DB is already up-to-date
// — we check by looking for the latest expected migration row, since the migration lock
// acquire path can spin for 90s on some systems even when no work is needed.
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
        await db.Database.MigrateAsync();

    // Seed built-in patterns and ignore rules
    var seeder = scope.ServiceProvider.GetRequiredService<SeedDataService>();
    await seeder.SeedAsync();

    // v4: ensure the two built-in roles exist. Idempotent — safe to call
    // every startup. AuthService.CreateInitialMasterAsync (driven by the
    // /setup wizard) references these by name.
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    await auth.EnsureBuiltInRolesAsync();

    // Seed built-in categories (Movies/Serials/Anime/Donghua/Dorama/Multi/Kids).
    // Idempotent — only inserts categories whose names aren't already present.
    var categories = scope.ServiceProvider.GetRequiredService<CategorySeedService>();
    await categories.EnsureSeedAsync();
}

// Appearance settings (language, theme, accent) are loaded client-side now
// — MainLayout in Animarr.UI fetches them from AppConfig on first render.
// The server no longer needs an in-process LocalizationService / ThemeService.

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Map modern font MIME types — Kestrel's default for .ttf is the obsolete
// `application/x-font-ttf` which some browsers refuse to apply. font/ttf is
// the current standard (RFC 8081).
// Never let the browser cache the HTML host page. It's the Blazor WASM
// bootstrap document (index.html); if it's cached, a deploy's updated asset
// references / inline cache-bust loader never reach the client until a manual
// hard-refresh — exactly the "I redeployed but the player looks unchanged"
// trap. With no-cache the browser revalidates index.html on every load, picks
// up the fresh document, and its per-load `?v=` loader then pulls the current
// player JS/CSS. Scoped to text/html only (set via OnStarting once the
// response content-type is known) so static JS/CSS/wasm keep their normal
// long-lived caching. Registered before UseStaticFiles so it wraps every
// downstream response, including the SPA fallback.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var ct = ctx.Response.ContentType;
        if (!string.IsNullOrEmpty(ct) &&
            ct.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] =
                "no-cache, no-store, must-revalidate";
            ctx.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Pragma] = "no-cache";
            ctx.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Expires] = "0";
        }
        return Task.CompletedTask;
    });
    await next();
});

var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".ttf"]   = "font/ttf";
contentTypes.Mappings[".woff"]  = "font/woff";
contentTypes.Mappings[".woff2"] = "font/woff2";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

// In .NET 9+, MapStaticAssets is the single source of truth for static asset
// routing — it surfaces every wwwroot/* file from this project AND from every
// ProjectReference'd RCL/WASM client through the static-asset manifest. It
// also serves Brotli/Gzip precompressed variants automatically.
//
// Calling UseBlazorFrameworkFiles() alongside this would register the same
// /_framework/* paths twice, the route table picks the empty-name one first,
// and the second middleware short-circuits without running the endpoint →
// "The request reached the end of the pipeline without executing the
// endpoint: ''". One call to MapStaticAssets is enough.
// AllowAnonymous: the login page itself is served from these assets, so they
// must bypass the default-deny fallback policy.
app.MapStaticAssets().AllowAnonymous();

// v4 auth middleware — must come BEFORE endpoint mappings so the cookie is
// resolved into HttpContext.User before [Authorize] checks run.
app.UseAuthentication();
app.UseAuthorization();
// Endpoint-scoped limiter policies (TV pairing). Must run before endpoint
// execution so RequireRateLimiting metadata is honoured.
app.UseRateLimiter();

// ─── Animarr.Shared REST surface ──────────────────────────────────────────
// Each endpoint group backs one slice of the IAnimarrApiClient contract that
// Animarr.UI / Animarr.Web.Client / Animarr.App all consume.
app.MapAuthEndpoints();
app.MapPairEndpoints();
app.MapUsersEndpoints();
app.MapCategoryEndpoints();
app.MapLlmEndpoints();
app.MapFolderEndpoints();
app.MapMediaEndpoints();
app.MapWatchStateEndpoints();
app.MapRecsEndpoints();
app.MapCalendarEndpoints();
app.MapFranchiseEndpoints();
app.MapTorrentEndpoints();
app.MapRenameEndpoints();
app.MapMediaTagEndpoints();
app.MapAppConfigEndpoints();
app.MapMetadataLanguageEndpoints();
app.MapSearchEndpoints();
app.MapDlnaCastEndpoints();
// v5 multi-server: anonymous /api/server/info probe used by Discovery.
app.MapServerInfoEndpoints();

// SignalR hubs — push-only telemetry for torrents + identification queue.
// AllowAnonymous: the MAUI apps connect with a bare HubConnection that carries
// no auth cookie (only the app's REST HttpClient holds the cookie jar), so a
// required-auth hub would silently break native live updates. The hubs never
// accept client invocations — they only broadcast status snapshots.
app.MapHub<TorrentHub>(Animarr.Shared.ApiRoutes.HubTorrents).AllowAnonymous();
app.MapHub<IdentificationHub>(Animarr.Shared.ApiRoutes.HubIdentification).AllowAnonymous();


// ─── Media byte surface — extracted from Program.cs into endpoint files ───
// Image/video/file/playlist/probe/subtitle/external-tracks, HLS lifecycle,
// the DLNA MediaServer SOAP surface, and the external-player (mpv) hooks.
// All are AllowAnonymous + MediaPathValidator-gated (see each file).
app.MapMediaStreamEndpoints();
app.MapHlsEndpoints();
app.MapDlnaServerEndpoints();
app.MapExternalPlayerEndpoints();

// SPA fallback — anything not matched by an API endpoint, Razor page, or
// static file falls through to Animarr.Web.Client's index.html. The WASM
// router then claims the URL client-side. Keep this LAST so it doesn't
// shadow the explicit routes above. AllowAnonymous — the SPA handles the
// login flow itself, so the document must load for signed-out visitors.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
