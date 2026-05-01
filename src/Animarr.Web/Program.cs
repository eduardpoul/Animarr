using Animarr.Web.Components;
using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

// Bind AppSettings
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("AppSettings"));

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

// App services
builder.Services.AddScoped<SeedDataService>();
builder.Services.AddSingleton<IPatternMatchService, PatternMatchService>();
builder.Services.AddScoped<IRenameService, RenameService>();
builder.Services.AddScoped<IAppConfigService, AppConfigService>();
builder.Services.AddSingleton<FolderWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FolderWatcherService>());
builder.Services.AddHostedService<RenameQueueProcessorService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<LocalizationService>();
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

// Metadata & LLM services
builder.Services.AddScoped<TmdbClient>();
builder.Services.AddScoped<MalClient>();
builder.Services.AddScoped<ImdbSearchClient>();
builder.Services.AddScoped<MetadataService>();
builder.Services.AddScoped<ILlmService, MicrosoftAiLlmService>();
builder.Services.AddHostedService<IdentificationQueueProcessorService>();

// Blazor + FluentUI
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents();

var app = builder.Build();

// Apply EF Core migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    // Seed built-in patterns and ignore rules
    var seeder = scope.ServiceProvider.GetRequiredService<SeedDataService>();
    await seeder.SeedAsync();
}

// Load appearance settings persisted in the database (language, theme, accent colour)
var localization = app.Services.GetRequiredService<LocalizationService>();
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var themeService = app.Services.GetRequiredService<ThemeService>();
using (var appearanceScope = app.Services.CreateScope())
{
    var appCfg = appearanceScope.ServiceProvider.GetRequiredService<IAppConfigService>();

    var lang = await appCfg.GetAsync(AppConfigKeys.Language) ?? "en";
    await localization.LoadAsync(lang, env);

    var themeStr = await appCfg.GetAsync(AppConfigKeys.ThemeMode);
    if (themeStr is not null && Enum.TryParse<DesignThemeModes>(themeStr, out var themeMode))
        themeService.Set(themeMode);

    var accentStr = await appCfg.GetAsync(AppConfigKeys.AccentColor);
    if (accentStr is not null && Enum.TryParse<OfficeColor>(accentStr, out var accent))
        themeService.SetAccentColor(accent);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ─── /api/image — serve media images from arbitrary disk paths ────────────
// Security: path must resolve inside one of the registered FolderWatcher paths.
app.MapGet("/api/image", async (string path, long? t, IDbContextFactory<AppDbContext> dbFactory, HttpContext ctx) =>
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

    // Validate: the file must reside inside a registered FolderWatcher path
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

// ─── /api/video — stream video files with range support ───────────────────
app.MapGet("/api/video", async (string path, IDbContextFactory<AppDbContext> dbFactory) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest();

    string fullPath;
    try { fullPath = Path.GetFullPath(path); }
    catch { return Results.BadRequest(); }

    if (Directory.Exists(fullPath))
        return Results.BadRequest();

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
        return Results.Forbid();

    if (!File.Exists(fullPath))
        return Results.NotFound();

    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
    var mime = ext switch
    {
        ".mp4"           => "video/mp4",
        ".webm"          => "video/webm",
        ".mkv"           => "video/x-matroska",
        ".avi"           => "video/x-msvideo",
        ".mov"           => "video/quicktime",
        ".m4v"           => "video/x-m4v",
        ".wmv"           => "video/x-ms-wmv",
        ".ts" or ".m2ts" => "video/mp2t",
        _                => "video/octet-stream",
    };

    return Results.File(fullPath, mime, enableRangeProcessing: true);
})
.WithName("GetVideo")
.AllowAnonymous();

app.Run();
