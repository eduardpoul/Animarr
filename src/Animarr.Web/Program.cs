using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Endpoints;
using Animarr.Web.Hubs;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Segments;
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

// ─── /api/image — serve media images from arbitrary disk paths ────────────
// Security: path must resolve inside one of the registered FolderWatcher paths,
// OR inside the dedicated image cache (which lives next to the database, away
// from the user's media tree).
app.MapGet("/api/image", async (string path, long? t, IDbContextFactory<AppDbContext> dbFactory, MediaCachePaths cachePaths, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest();

    // Normalise and resolve to absolute
    string fullPath;
    try { fullPath = Path.GetFullPath(path); }
    catch { return Results.BadRequest(); }

    // Reject directory traversal — path must point to a file, not a directory
    if (Directory.Exists(fullPath))
        return Results.BadRequest();

    // C-6: reject symlinks (reparse points) so a symlink inside an allowed
    // folder cannot leak files from outside the library (e.g. /etc/shadow).
    try
    {
        if (File.Exists(fullPath))
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return Results.Forbid();
        }
    }
    catch { /* fall through to existence check below */ }

    // Validate: the file must reside inside a registered FolderWatcher path
    // OR inside Animarr's dedicated image cache (which lives outside the user's
    // media tree, in /app/data/image-cache by default).
    await using var db = await dbFactory.CreateDbContextAsync();
    var allowedRoots = await db.FolderWatchers
        .Select(f => f.Path)
        .ToListAsync();
    allowedRoots.Add(cachePaths.CacheRoot);

    bool allowed = allowedRoots.Any(root =>
    {
        var normalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase);
    });

    if (!allowed)
        return Results.Forbid();

    if (!File.Exists(fullPath))
        return Results.NotFound();

    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
    var mime = ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"            => "image/png",
        ".webp"           => "image/webp",
        ".gif"            => "image/gif",
        _                 => "application/octet-stream",
    };

    // Cache-busting: if a version timestamp was supplied (t != 0), the URL is
    // unique per file version → cache immutably for 1 year.
    // If t is absent or 0, use no-cache so the browser always revalidates.
    ctx.Response.Headers.CacheControl = (t is > 0)
        ? "public, max-age=31536000, immutable"
        : "no-cache";
    return Results.File(fullPath, mime);
})
.WithName("GetMediaImage")
.AllowAnonymous();

// ─── Shared whitelist check for video / probe / subtitle endpoints ─────────
// All three serve content from disk; we want one place to enforce the
// "must live under a registered section folder" invariant.
async Task<(bool ok, string? fullPath, IResult? earlyResult)> ResolveAllowedPathAsync(
    string? path, IDbContextFactory<AppDbContext> dbFactory)
{
    if (string.IsNullOrWhiteSpace(path))
        return (false, null, Results.BadRequest());

    string fullPath;
    try { fullPath = Path.GetFullPath(path); }
    catch { return (false, null, Results.BadRequest()); }

    if (Directory.Exists(fullPath))
        return (false, null, Results.BadRequest());

    await using var db = await dbFactory.CreateDbContextAsync();
    var allowedRoots = await db.FolderWatchers
        .Select(f => f.Path)
        .ToListAsync();

    bool allowed = allowedRoots.Any(root =>
    {
        var normalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase);
    });

    if (!allowed)
        return (false, null, Results.Forbid());

    if (!File.Exists(fullPath))
        return (false, null, Results.NotFound());

    return (true, fullPath, null);
}

// Browser-friendly containers we serve raw (with Range). Anything else is
// piped through `ffmpeg -c copy -f mp4` for in-browser playback. Codec inside
// MKV is irrelevant to the remux — H.264/H.265/AV1/VP9 all stream-copy fine.
// HEVC compatibility is a CLIENT concern (system codec / HW decode); we just
// repackage. Same for AAC/AC3 — pass-through.
static bool IsBrowserNativeContainer(string ext) =>
    ext is ".mp4" or ".m4v" or ".mov" or ".webm";

// Mime type for a video file by extension. Used by both the raw /api/file
// endpoint and the DLNA item DIDL — TVs match the protocolInfo string
// against their decoder capabilities, so getting "video/x-matroska" right
// for .mkv matters.
static string MimeForVideoExt(string ext) => ext.ToLowerInvariant() switch
{
    ".mp4"  => "video/mp4",
    ".m4v"  => "video/x-m4v",
    ".mov"  => "video/quicktime",
    ".webm" => "video/webm",
    ".mkv"  => "video/x-matroska",
    ".avi"  => "video/x-msvideo",
    ".ts"   => "video/mp2t",
    ".m2ts" => "video/mp2t",
    ".wmv"  => "video/x-ms-wmv",
    ".flv"  => "video/x-flv",
    ".ogv"  => "video/ogg",
    _       => "application/octet-stream",
};

// Quick ffprobe peek for the first video stream so /api/video can branch on
// codec / Dolby-Vision presence. Returns ("hevc", true) for HDR10-compatible
// DV profile 8 files like the user's "Hail Mary 2026.HDRP-DVT.mkv".
static async Task<(string? codec, bool hasDolbyVision)> PeekVideoCodecAsync(string fullPath)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name:stream_side_data=side_data_type",
            "-print_format", "default=noprint_wrappers=1:nokey=0",
            fullPath,
        }) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return (null, false);
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) return (null, false);

        string? codec = null;
        bool dv = false;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("codec_name=", StringComparison.Ordinal))
                codec = line["codec_name=".Length..].Trim();
            else if (line.StartsWith("side_data_type=", StringComparison.Ordinal)
                  && line.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                dv = true;
        }
        return (codec, dv);
    }
    catch
    {
        return (null, false);
    }
}

// ─── /api/video — stream video files for in-browser playback ──────────────
// MP4 / WebM:   served as a static file with HTTP Range support.
// MKV / AVI /…: remuxed on-the-fly to fragmented MP4 via ffmpeg stream-copy.
//               No transcode → ~0% CPU, near-line-rate. Browser plays the
//               fMP4 stream natively through MSE.
app.MapMethods("/api/video", new[] { "GET", "HEAD" }, async (
        string path,
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory,
        ILoggerFactory loggerFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    var ext = Path.GetExtension(fullPath!).ToLowerInvariant();
    if (IsBrowserNativeContainer(ext))
    {
        var mime = ext switch
        {
            ".mp4"  => "video/mp4",
            ".m4v"  => "video/x-m4v",
            ".mov"  => "video/quicktime",
            ".webm" => "video/webm",
            _       => "video/octet-stream",
        };
        return Results.File(fullPath!, mime, enableRangeProcessing: true);
    }

    // HEAD on the remux endpoint — Vidstack probes the resource before GET to
    // sniff content-type and Range support. Spinning up ffmpeg just to throw
    // the bytes away is wasteful, so reply with headers only. No length (live
    // stream), no Accept-Ranges (browser can't pre-seek the pipe; it can ask
    // for ?seek=N as a query param instead).
    if (HttpMethods.IsHead(http.Request.Method))
    {
        http.Response.ContentType = "video/mp4";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Empty;
    }

    // Remux path. Stream ffmpeg's stdout to the HTTP response body so the
    // browser starts playing as bytes arrive. No Range support here — MSE
    // buffers what it needs; seeking past buffer triggers a fresh request
    // (the client can pass ?seek=N to ask ffmpeg to start later in the file).
    var seekSec = 0d;
    if (http.Request.Query.TryGetValue("seek", out var seekStr) &&
        double.TryParse(seekStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0)
        seekSec = s;

    var logger = loggerFactory.CreateLogger("ApiVideoRemux");
    var args = new List<string>();
    if (seekSec > 0)
    {
        // -ss BEFORE -i = fast input seek (keyframe-accurate from container index).
        // Cheap on MKV (Matroska has cue points) and works for stream-copy.
        args.Add("-ss"); args.Add(seekSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }
    // Inspect the video codec once so we can apply codec-specific fixes:
    //  • HEVC needs `-tag:v hvc1` — Chrome/Edge refuse the default `hev1` tag
    //    in fragmented MP4. Same bytes, different fourcc, plays fine.
    //  • DolbyVision-flagged HEVC (profile 8.1 = HDR10-compatible) carries a
    //    DOVI configuration box that browsers can't parse. Strip it via the
    //    hevc_metadata bitstream filter so the player sees plain HDR10.
    var (videoCodec, hasDolbyVision) = await PeekVideoCodecAsync(fullPath!);

    args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath! });
    if (seekSec > 0)
    {
        // Shift output PTS so the first frame is at time `seekSec`, not 0.
        // Without this, every seek-reload would reset the timeline to 0:00,
        // confusing both the player UI and our progress-tracking math.
        args.Add("-output_ts_offset");
        args.Add(seekSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }
    args.AddRange(new[]
    {
        "-map", "0:v:0?",            // first video stream if present
        "-map", "0:a:0?",            // first audio stream only — multi-audio
                                     // selection in fMP4 isn't browser-friendly
        "-c:v", "copy",              // video stream-copy
        // Audio: transcode to AAC. Source can be AC3 / EAC3 / DTS / TrueHD —
        // none of those play reliably in Chrome/Edge. AAC is the lowest common
        // denominator that every browser decodes. 192 kbps stereo is plenty
        // for downmixed surround → headphones / laptop speakers; if the user
        // ever wants 5.1 pass-through we'll add a query toggle.
        "-c:a", "aac",
        "-ac", "2",
        "-b:a", "192k",
    });
    if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
    {
        // Critical for Chrome/Edge: refuse the default `hev1` fourcc in fMP4.
        args.Add("-tag:v"); args.Add("hvc1");
    }
    args.AddRange(new[]
    {
        "-f", "mp4",
        // Note on DolbyVision: profile 8.1 files are HDR10-backward-compatible
        // at the HEVC layer, so most browsers can play the stream as-is once
        // the codec tag is hvc1. We do NOT strip the DV RPU here because
        // `hevc_metadata=remove_dovi=1` requires ffmpeg >= 7.1, while the
        // Debian-bundled build is 5.1. If DV-flagged playback breaks again
        // we'll bring our own ffmpeg or transcode.
        // delay_moov: required because AC3 audio packets don't expose their
        // codec frame size until the first frame is muxed; without this flag
        // ffmpeg refuses to emit the (empty) moov and the response is 0 bytes.
        "-movflags", "+frag_keyframe+empty_moov+delay_moov+default_base_moof+separate_moof",
        "pipe:1",
    });

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName               = "ffmpeg",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);

    System.Diagnostics.Process? proc = null;
    try { proc = System.Diagnostics.Process.Start(psi); }
    catch (Exception ex)
    {
        logger.LogError(ex, "ffmpeg launch failed for {Path}", fullPath);
        return Results.StatusCode(500);
    }
    if (proc is null) return Results.StatusCode(500);

    // Drain stderr in background so the buffer never blocks. Log warnings —
    // ffmpeg's stream-copy is normally silent so anything here is interesting.
    _ = Task.Run(async () =>
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                logger.LogWarning("ffmpeg: {Line}", line);
        }
        catch { /* process exited */ }
    });

    // When the client disconnects (closes tab / seeks past buffer / etc.)
    // the response stream errors out; kill ffmpeg so we don't leak zombies.
    http.RequestAborted.Register(() =>
    {
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
    });

    http.Response.ContentType = "video/mp4";
    http.Response.Headers.CacheControl = "no-store";
    try
    {
        await proc.StandardOutput.BaseStream.CopyToAsync(http.Response.Body, http.RequestAborted);
    }
    catch (OperationCanceledException) { /* client gone */ }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "stream pump failed for {Path}", fullPath);
    }
    finally
    {
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
        proc.Dispose();
    }
    return Results.Empty;
})
.WithName("GetVideo")
.AllowAnonymous();

// ─── /api/file — raw passthrough for DLNA / external native players ──────
// Streams the source file byte-for-byte with HTTP Range support. No demux,
// no transcode, no re-mux — TVs, VLC, Kodi, Infuse get bit-identical
// source content and decode with their own hardware. This is what /api/dlna
// uses for the cast URL it hands to MediaRenderers, and what DLNA browse
// returns as item.res URLs. Browsers can use this too for MP4/WebM but
// typically can't decode MKV containers, hence /api/video stays around as
// the browser-friendly path that re-muxes.
app.MapMethods("/api/file", new[] { "GET", "HEAD" }, async (
        string path,
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    var ext = Path.GetExtension(fullPath!).ToLowerInvariant();
    var mime = MimeForVideoExt(ext);

    // Cache headers — same file always same content; clients can cache
    // aggressively. The path itself is the cache key because file rename
    // changes the URL.
    http.Response.Headers.CacheControl = "public, max-age=86400";
    http.Response.Headers.AcceptRanges = "bytes";
    // DLNA hint headers — some renderers require these to enable seeking.
    http.Response.Headers["TransferMode.DLNA.ORG"] = "Streaming";
    http.Response.Headers["ContentFeatures.DLNA.ORG"] =
        "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    return Results.File(fullPath!, mime, enableRangeProcessing: true);
})
.WithName("GetRawFile")
.AllowAnonymous();

// ─── /api/playlist.m3u — universal external-player handoff ────────────────
// Returns a tiny .m3u file pointing at /api/file for the requested path.
// Triggered by the "External Player" icon when the user has picked "m3u"
// as their default in Settings — every desktop player (VLC, MPC-HC,
// PotPlayer, mpv standalone, IINA, etc.) opens .m3u files via OS file
// association, so this is the most portable handoff that requires no
// per-player URI scheme. Browser downloads the file → user double-clicks
// or it auto-opens depending on download settings.
app.MapGet("/api/playlist.m3u", async (
        string path,
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    var fileName     = Path.GetFileNameWithoutExtension(fullPath!);
    var safeFileName = string.Concat(fileName.Where(c => !char.IsControl(c) && c != '"'));
    // Build absolute stream URL — players opening the .m3u won't know our
    // server's hostname otherwise. Use plain HTTP on the raw 8080 port so
    // Caddy's self-signed cert doesn't trip up non-browser TLS stacks.
    var scheme = "http";
    var host   = http.Request.Host.Host;
    var port   = "8080";
    var streamUrl = $"{scheme}://{host}:{port}/api/file?path={Uri.EscapeDataString(fullPath!)}";

    var m3u =
        "#EXTM3U\n" +
        $"#EXTINF:-1,{safeFileName}\n" +
        $"{streamUrl}\n";

    http.Response.Headers.ContentDisposition =
        $"attachment; filename=\"{safeFileName}.m3u\"";
    return Results.Content(m3u, "audio/x-mpegurl");
})
.WithName("PlaylistM3u")
.AllowAnonymous();

// ─── /api/hls — HLS streaming for in-browser playback ─────────────────────
// Lifecycle:
//   POST /api/hls/start?path=X[&seek=N]    → { token, manifestUrl }
//   GET  /api/hls/{token}/master.m3u8      → playlist
//   GET  /api/hls/{token}/seg-NNNNN.{m4s|ts} → segment (extension per plan)
//   POST /api/hls/keepalive?token=T        → bump idle timer
//   DELETE /api/hls/{token}                → stop session + free /tmp
//
// HlsSessionService picks one of three plans per source: MPEG-TS stream-copy
// for H.264 (most files; PCR-sync, no GPU), fMP4 + VAAPI re-encode for HEVC
// 8-bit (tight sync via -bf 0), or fMP4 stream-copy fallback for HEVC 10-bit
// HDR/DV. The endpoint above is plan-agnostic — the manifest tells the
// player what to fetch and we just serve files from the session dir.
app.MapPost("/api/hls/start", async (
        string path,
        double? seek,
        int? audioOffsetHwMs,
        int? audioOffsetSwMs,
        // 0 = first audio stream (historical default). Surfaced via the HUD's
        // Audio popup which restarts the session with a different index when
        // the user picks another track — there's no in-stream switching with
        // our single-stream transcode pipeline.
        int? audioTrackIndex,
        // Cap output height (1080 / 720 / …). 0 or absent = native resolution.
        // Below the source height it forces a re-encode/downscale. Surfaced via
        // the HUD's Quality popup, which restarts the session with a new cap.
        int? maxHeight,
        // Absolute path to an external sideload audio file (a dub track that
        // isn't muxed in the source). When set, the session muxes it as a
        // second ffmpeg input and Direct Play is skipped (we must transcode to
        // combine the foreign audio with the source video). Discovered by
        // /api/external-tracks; re-validated here against the library roots.
        string? externalAudio,
        // Set by the client (MediaSource.isTypeSupported) when the browser can
        // decode HEVC. Lets the server Direct-Stream HEVC (stream-copy, original
        // quality) instead of re-encoding it to H.264. Absent → re-encode.
        // NB: bound as string, not bool — the client sends "1", which minimal
        // API's bool binder rejects with a 400 (it only accepts true/false).
        string? clientHevc,
        // Same flag for 10-bit / Main10 HEVC — gates Direct Play of HDR10 MP4s
        // (so they play on a native <video> where RTX VSR / HDR can engage).
        string? clientHevc10,
        // Cap output bitrate in Mbps (player Bitrate menu). >0 forces a re-encode
        // at this bitrate; absent/0 = no cap.
        int? maxBitrate,
        IDbContextFactory<AppDbContext> dbFactory,
        HlsSessionService hls,
        ILoggerFactory loggerFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    // Validate the external audio path the same way (must be inside a watched
    // root + exist). On failure we DON'T fail playback — we just drop the dub
    // and play the source's own audio, which is the least-surprising fallback
    // for a stale path.
    string? externalAudioFull = null;
    if (!string.IsNullOrWhiteSpace(externalAudio))
    {
        var (extOk, extFull, _) = await ResolveAllowedPathAsync(externalAudio, dbFactory);
        if (extOk) externalAudioFull = extFull;
        else loggerFactory.CreateLogger("ApiHls")
                 .LogWarning("HLS start: ignoring out-of-bounds external audio {Path}", externalAudio);
    }

    try
    {
        var seekSec = seek is > 0 ? seek.Value : 0d;

        // Direct Play probe: if the source file is already in a browser-
        // native format (MP4/H.264/AAC), skip HLS entirely and have the
        // player point at /api/file. Zero transcode, instant start, perfect
        // sync. This is Plex/Jellyfin's "Direct Play" tier — the first
        // choice the server makes before falling back to transcoding.
        // Skip the Direct Play probe entirely when an external dub is in play:
        // combining foreign audio with the source video requires the HLS mux
        // path, so a direct file URL would silently drop the dub.
        bool wantHevc   = clientHevc   == "1" || string.Equals(clientHevc,   "true", StringComparison.OrdinalIgnoreCase);
        bool wantHevc10 = clientHevc10 == "1" || string.Equals(clientHevc10, "true", StringComparison.OrdinalIgnoreCase);
        // A height/bitrate cap (the HUD's Quality menu) forces a re-encode —
        // you can't shrink a stream-copy — so it must bypass BOTH native paths
        // (Direct Play AND Direct Stream) and fall through to the HLS pipeline
        // where maxHeight/maxBitrate actually apply. Same for an external dub,
        // which needs the HLS mux to combine foreign audio with the video.
        bool wantCap = (maxHeight ?? 0) > 0 || (maxBitrate ?? 0) > 0;
        var decision = externalAudioFull is null && !wantCap
            ? await hls.ChoosePlaybackAsync(fullPath!, wantHevc, wantHevc10)
            : new HlsSessionService.PlaybackDecision(false, null, 0, null);
        if (decision.DirectPlay && decision.DirectUrl is not null)
        {
            return Results.Ok(new
            {
                // Direct Play response: client checks for this field and
                // routes around the HLS-specific setup (no session token,
                // no keepalive, no audio-sync calibration).
                directPlayUrl = decision.DirectUrl,
                totalDuration = decision.DurationSec,
                resumeSec     = seekSec,
                // What the player actually receives — for the HUD plashka.
                // Direct Play means source passes through unchanged, so HDR
                // tags etc. are preserved here.
                output        = decision.Output,
            });
        }

        if (decision.DirectStream && decision.DirectUrl is not null)
        {
            return Results.Ok(new
            {
                // Direct Stream response: /api/video remuxes the source to
                // progressive fMP4 on the fly (video copy + audio→AAC). The
                // client plays it on a native <video> — no HLS session, no
                // keepalive — and seeks by re-requesting at ?seek=N. Video
                // bitstream + HDR pass through unchanged.
                directStreamUrl = decision.DirectUrl,
                totalDuration   = decision.DurationSec,
                resumeSec       = seekSec,
                output          = decision.Output,
            });
        }

        // Per-client audio offsets — two channels, server picks which one to
        // apply based on the plan it chooses for this file:
        //   • HW = VAAPI re-encode path (HEVC 8-bit). Has near-zero baseline
        //     wobble thanks to -bf 0, but adds browser/decoder chain latency.
        //   • SW = fMP4 stream-copy path (HEVC 10-bit / HDR / DV). B-frames
        //     preserved → different residual wobble that needs its own value.
        // Both come from independent sliders in Settings; clamp to ±500ms so
        // a runaway client can't push audio half a second out of sync.
        // Clamp envelopes are asymmetric:
        //   HW (VAAPI re-encode) — ±500ms is plenty, decoder chain latency
        //     varies but never lands in three-digit-positive territory once
        //     -bf 0 strips B-frames.
        //   SW (fMP4 stream-copy) — UHD BluRay remuxes with B15 GOP structures
        //     produce ~625ms of audio-ahead at 23.976fps, so we allow up to
        //     +800ms. Going lower than -200ms makes no physical sense (audio
        //     would have to play BEFORE the file's first frame), so the
        //     negative side stays tight.
        var audioOffsetHwSec = audioOffsetHwMs is int hwMs
            ? Math.Clamp(hwMs, -500, 500) / 1000.0
            : 0.07;  // default matches DEFAULT_OFFSET_MS in calibrate.js
        var audioOffsetSwSec = audioOffsetSwMs is int swMs
            ? Math.Clamp(swMs, -200, 800) / 1000.0
            : 0.0;   // SW path has no sensible default — user must dial it in
        var result = await hls.StartAsync(fullPath!, seekSec,
            audioOffsetHwSec: audioOffsetHwSec,
            audioOffsetSwSec: audioOffsetSwSec,
            audioTrackIndex:  Math.Max(0, audioTrackIndex ?? 0),
            maxHeight:        Math.Max(0, maxHeight ?? 0),
            externalAudioPath: externalAudioFull,
            clientHevc:       wantHevc,
            maxBitrate:       Math.Max(0, maxBitrate ?? 0));
        return Results.Ok(new
        {
            token        = result.Token,
            manifestUrl  = result.ManifestRelativeUrl,
            // Full file duration in seconds. The JS bridge uses this to
            // report progress against the real runtime, since the playlist's
            // own duration is shortened to (totalDur - resumeSec) and the
            // player only knows about the shortened timeline.
            totalDuration = result.TotalDurationSec,
            resumeSec    = seekSec,
            audioOffsetHwMs = (int)Math.Round(audioOffsetHwSec * 1000),
            audioOffsetSwMs = (int)Math.Round(audioOffsetSwSec * 1000),
            // What the player actually receives — drives the HUD plashka.
            // For re-encode plans this differs from /api/probe output (e.g.
            // HEVC 10-bit DV source → H.264 8-bit SDR here).
            output       = result.Output,
        });
    }
    catch (Exception ex)
    {
        var logger = loggerFactory.CreateLogger("ApiHls");
        logger.LogError(ex, "HLS start failed for {Path}", fullPath);
        return Results.Problem("Failed to start HLS session.", statusCode: 500);
    }
})
.WithName("StartHls")
.AllowAnonymous();

app.MapGet("/api/hls/{token}/{file}", async (
        string token,
        string file,
        HttpContext http,
        HlsSessionService hls) =>
{
    // Token is a hex GUID; file must be a flat filename (defence-in-depth
    // against path traversal even though session dir is already isolated).
    if (token.Length is < 16 or > 64 || token.Any(c => !Uri.IsHexDigit(c)))
        return Results.NotFound();
    var bare = Path.GetFileName(file);
    if (string.IsNullOrEmpty(bare) || bare != file) return Results.NotFound();

    var dir = hls.GetSessionDir(token);
    if (dir is null) return Results.NotFound();

    var probePath = Path.Combine(dir, bare);
    // Defence-in-depth: GetFileName already strips path separators above,
    // but if the session dir somehow became a symlink (admin moved /tmp,
    // tmpfs trickery, etc.) the resolved path could escape. Verify the
    // canonical full path stays inside the session dir before serving.
    var canonicalProbe = Path.GetFullPath(probePath);
    var canonicalDir   = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!canonicalProbe.StartsWith(canonicalDir, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    // Wait for ffmpeg to catch up if the file hasn't landed yet — the player
    // requests segments according to our pre-written VOD playlist, so for
    // points beyond ffmpeg's current encoding position we wait up to 30s
    // (HlsSessionService.SegmentWaitTimeout). Past that we 503 so hls.js
    // retries with backoff instead of bailing the stream.
    var full = await hls.WaitForFileAsync(token, bare, http.RequestAborted);
    if (full is null) return Results.StatusCode(503);

    // Two categories with different caching/transfer semantics:
    //
    //   • Playlists (.m3u8): small text, MUTABLE while ffmpeg encodes. We
    //     read the whole file on each hit and return it wholesale — Range
    //     requests against a mutable file race against ffmpeg rewrites and
    //     produce spurious 416 (browser cached size N, file is now size M < N,
    //     Range bytes=N- becomes unsatisfiable). Cheap because playlists are
    //     a few hundred bytes.
    //
    //   • Segments (.m4s/.ts/init.mp4): big binary, IMMUTABLE once flushed.
    //     Range support is essential — players seek by byte-range into the
    //     middle of a segment when scrubbing.
    var isPlaylist = bare.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    if (isPlaylist)
    {
        var bytes = await File.ReadAllBytesAsync(full);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(bytes, "application/vnd.apple.mpegurl");
    }

    http.Response.Headers.CacheControl = "public, max-age=86400, immutable";
    // MIME type: fMP4 segments are application/iso.segment (a.k.a. video/iso.
    // segment), MPEG-TS segments are video/MP2T. Browsers ignore both for
    // hls.js-driven playback (Content-Type is set on the master playlist
    // instead) but smart TVs and Chromecast inspect segment Content-Type
    // before deciding whether to attempt Direct Play, so we set it correctly.
    var isTsSegment = bare.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
    var mime = isTsSegment ? "video/MP2T" : "video/iso.segment";
    return Results.File(full, mime, enableRangeProcessing: true);
})
.WithName("GetHlsFile")
.AllowAnonymous();

app.MapPost("/api/hls/keepalive", (string token, HlsSessionService hls) =>
        hls.Touch(token) ? Results.NoContent() : Results.NotFound())
    .WithName("HlsKeepalive")
    .AllowAnonymous();

app.MapDelete("/api/hls/{token}", (string token, HlsSessionService hls) =>
{
    hls.Stop(token);
    return Results.NoContent();
})
.WithName("StopHls")
.AllowAnonymous();

// Diagnostic: list every active session with ffmpeg state + segment progress.
// Hit this when a player is stuck on a frame to see whether the backing
// session is alive, crashed, or completed-partial. Admin-gated — the snapshot
// exposes library file paths and lets the caller correlate who watches what.
app.MapGet("/api/hls/sessions", (HlsSessionService hls) =>
    Results.Ok(hls.Snapshot()))
    .WithName("ListHlsSessions")
    .RequireAuthorization(AuthConstants.Policies.SystemSettings);

// NOTE: /api/hardware-info used to be mapped here TOO — AppConfigEndpoints
// maps the same route, and two endpoints on one route+method make ASP.NET
// throw AmbiguousMatchException (HTTP 500) on every request. The endpoint
// now lives only in AppConfigEndpoints.cs.

// Nuke everything — useful when a runaway tab piled up stale sessions and
// the user wants a clean slate without restarting the container. Admin-gated:
// anonymous callers must not be able to kill every active playback on the box.
app.MapDelete("/api/hls/sessions", (HlsSessionService hls) =>
{
    var n = 0;
    foreach (var s in hls.Snapshot()) { hls.Stop(s.Token); n++; }
    return Results.Ok(new { stopped = n });
})
.WithName("StopAllHlsSessions")
.RequireAuthorization(AuthConstants.Policies.SystemSettings);

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

// NOTE: /api/dlna/renderers + /api/dlna/play used to be mapped inline here
// TOO — DlnaCastEndpoints maps the same routes, and two endpoints on one
// route+method make ASP.NET throw AmbiguousMatchException (HTTP 500) on
// every request, which silently broke "Send to TV". The control-point
// endpoints now live only in DlnaCastEndpoints.cs (with this version's
// path validation + AdvertisedHost URL building folded in).

// ─── /api/probe — return ffprobe stream list as JSON for player UI menus ──
app.MapGet("/api/probe", async (
        string path,
        IDbContextFactory<AppDbContext> dbFactory,
        ILoggerFactory loggerFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    var logger = loggerFactory.CreateLogger("ApiProbe");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName               = "ffprobe",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    foreach (var a in new[]
    {
        "-v", "error",
        "-print_format", "json",
        "-show_format", "-show_streams",
        fullPath!,
    }) psi.ArgumentList.Add(a);

    try
    {
        using var p = System.Diagnostics.Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            logger.LogWarning("ffprobe non-zero for {Path}: {Err}", fullPath, err);
            return Results.StatusCode(500);
        }
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "ffprobe failed for {Path}", fullPath);
        return Results.StatusCode(500);
    }
})
.WithName("GetProbe")
.AllowAnonymous();

// ─── /api/external-tracks — sideload audio/subtitle files for a video ─────
// Deterministic Tier-0 (sidecar) + Tier-1 (episode bucket) discovery — see
// ExternalTrackService. Returns ExternalTrackDto[]; the player merges audio
// entries into the Audio picker (played via a second ffmpeg input) and
// subtitle entries into the CC picker (converted by /api/subtitle).
app.MapGet(Animarr.Shared.ApiRoutes.ExternalTracks, async (
        string path,
        IDbContextFactory<AppDbContext> dbFactory,
        ExternalTrackService externalTracks,
        CancellationToken ct) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;
    var tracks = await externalTracks.FindForVideoAsync(fullPath!, ct);
    return Results.Ok(tracks);
})
.WithName("GetExternalTracks")
.AllowAnonymous();

// ─── /api/subtitle — extract one subtitle track as VTT (default) or ASS ───
// Browsers only render WebVTT natively via <track>. For ASS/SSA (anime
// fansubs) the front-end uses libass-wasm and asks for `?format=ass` here.
app.MapGet("/api/subtitle", async (
        string path,
        int track,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext http,
        ILoggerFactory loggerFactory) =>
{
    var (ok, fullPath, earlyResult) = await ResolveAllowedPathAsync(path, dbFactory);
    if (!ok) return earlyResult!;

    var format = (http.Request.Query["format"].ToString() ?? "").ToLowerInvariant();
    var (outFormat, contentType) = format switch
    {
        "ass" or "ssa" => ("ass",  "text/x-ssa; charset=utf-8"),
        _              => ("webvtt", "text/vtt; charset=utf-8"),
    };

    var logger = loggerFactory.CreateLogger("ApiSubtitle");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName               = "ffmpeg",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    foreach (var a in new[]
    {
        "-hide_banner", "-loglevel", "warning",
        "-i", fullPath!,
        "-map", $"0:s:{track}",
        "-f", outFormat,
        "pipe:1",
    }) psi.ArgumentList.Add(a);

    try
    {
        using var p = System.Diagnostics.Process.Start(psi)!;
        http.Response.ContentType = contentType;
        http.Response.Headers.CacheControl = "public, max-age=3600";
        await p.StandardOutput.BaseStream.CopyToAsync(http.Response.Body, http.RequestAborted);
        await p.WaitForExitAsync();
        return Results.Empty;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "ffmpeg subtitle extract failed for {Path} track {Track}", fullPath, track);
        return Results.StatusCode(500);
    }
})
.WithName("GetSubtitle")
.AllowAnonymous();

// ─── /api/watch/external-progress — progress pings from external players ─
// Used by mpv's animarr-tracker.lua script (installed once into mpv's
// scripts/ dir). Resolves the file path back to a MediaItem + (season,
// episode) and writes a WatchState row exactly as the in-browser player
// would. Lets users open files in mpv for native HDR/Atmos/DV playback
// without losing the Continue / % watched bookkeeping.
app.MapPost("/api/watch/external-progress", async (
        ExternalProgressRequest body,
        IDbContextFactory<AppDbContext> dbFactory,
        IPatternMatchService patternMatch,
        IWatchStateService watchSvc,
        ILoggerFactory loggerFactory,
        CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("ExternalProgress");
    if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest("path required");

    var (ok, fullPath, early) = await ResolveAllowedPathAsync(body.Path, dbFactory);
    if (!ok) return early!;

    // Resolve which MediaItem owns this path. Two cases:
    //   • Movie (SingleFilePath set): match path EXACTLY
    //   • Series / per-title folder: file lives under folder.Path
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var items = await db.MediaItems.Include(m => m.Folder).ToListAsync(ct);

    MediaItem? owner = null;
    foreach (var m in items)
    {
        if (m.Folder is null) continue;
        if (!string.IsNullOrEmpty(m.Folder.SingleFilePath)
            && string.Equals(m.Folder.SingleFilePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            owner = m; break;
        }
        if (!string.IsNullOrEmpty(m.Folder.Path) && !m.Folder.IsSection)
        {
            var folderRoot = m.Folder.Path
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullPath!.StartsWith(folderRoot, StringComparison.OrdinalIgnoreCase))
            {
                owner = m; break;
            }
        }
    }
    if (owner is null)
    {
        logger.LogDebug("No MediaItem matches external play path {Path}", fullPath);
        return Results.NotFound("No catalog item owns this path");
    }

    int? season = null, episode = null;
    if (owner.MediaType != MediaItemType.Movie)
    {
        // Parse season/episode from filename using the same pattern engine
        // that drives rename suggestions — fileNameWithoutExt → (season, ep).
        var rules = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);
        var parsed = patternMatch.ParseFileName(Path.GetFileName(fullPath!), rules);
        if (parsed.IsMatched && parsed.Episode > 0)
        {
            // Season comes from filename or falls back to folder-path detection.
            season  = parsed.Season ?? patternMatch.DetectSeasonFromPath(
                Path.GetDirectoryName(fullPath!) ?? "", owner.Folder?.Path) ?? 1;
            episode = parsed.Episode;
        }
    }

    var positionMs = (long)Math.Max(0, body.PositionSec * 1000);
    var runtimeMs  = body.DurationSec is > 0 ? (long?)(body.DurationSec * 1000) : null;
    // Delta tracking — clamp to reasonable per-tick increments so a stuck
    // mpv that keeps reporting position 0 doesn't inflate TotalWatchTimeSec.
    var delta = Math.Clamp(body.PlayedDeltaSec ?? 5, 0, 30);

    await watchSvc.RecordProgressAsync(owner.Id, season, episode, fullPath,
        positionMs, runtimeMs, delta, ct);

    return Results.NoContent();
})
.WithName("ExternalProgress")
.AllowAnonymous();

// ─── /api/mpv-tracker.lua — installable mpv script with baked-in URL ────
// User downloads this once, drops into mpv's scripts/ dir. Reports playback
// position to /api/watch/external-progress every 5 seconds, plus on end-of-
// file so the final position is captured even when mpv is closed abruptly.
app.MapGet("/api/mpv-tracker.lua", (HttpContext http, DlnaService dlna) =>
{
    // Use the same advertised origin as DLNA — that's the URL the user
    // already knows reaches Animarr from their LAN.
    var animarrUrl = dlna.AdvertisedHost ?? $"http://{http.Request.Host}";
    var lua = MpvTrackerScript.Build(animarrUrl);
    http.Response.Headers["Content-Disposition"] = "attachment; filename=\"animarr-tracker.lua\"";
    return Results.Text(lua, "text/x-lua; charset=utf-8");
})
.WithName("MpvTrackerScript")
.AllowAnonymous();

// SPA fallback — anything not matched by an API endpoint, Razor page, or
// static file falls through to Animarr.Web.Client's index.html. The WASM
// router then claims the URL client-side. Keep this LAST so it doesn't
// shadow the explicit routes above. AllowAnonymous — the SPA handles the
// login flow itself, so the document must load for signed-out visitors.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// ─── Request DTOs ─────────────────────────────────────────────────────────

/// <summary>POST body for /api/watch/external-progress (sent by mpv lua script).</summary>
public record ExternalProgressRequest(
    string? Path,
    double PositionSec,
    double? DurationSec,
    int? PlayedDeltaSec);
