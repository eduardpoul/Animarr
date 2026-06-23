using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Built-in TMDB credentials so Animarr works out of the box without every
/// self-hosted user registering their own key — the same approach Jellyfin and
/// Emby take (one shared application key baked into the app).
///
/// The key lives in configuration under <c>Metadata:TmdbApiKey</c>
/// (appsettings.json). <see cref="BuiltInApiKey"/> is populated once at startup
/// in Program.cs from that value.
///
/// Effective-key resolution (see <see cref="AppConfigTmdbExtensions.GetTmdbApiKeyAsync"/>),
/// highest priority first:
///   1. User-configured key (Settings → API Keys), stored per-instance in the DB.
///   2. <see cref="BuiltInApiKey"/> — from appsettings.json, or overridden at deploy
///      time by the <c>Metadata__TmdbApiKey</c> environment variable (recommended
///      when the repo is public, so the literal in appsettings.json can stay empty).
///
/// NOTE: a baked-in key is shared and low-trust. If it is ever rate-limited or
/// revoked, any user can override it in Settings with no code change.
/// </summary>
public static class TmdbDefaults
{
    /// <summary>
    /// Built-in TMDB key — a v3 API key (32 hex chars) or a v4 read access token
    /// (starts with "eyJ"). Populated once at startup from <c>Metadata:TmdbApiKey</c>;
    /// null/empty when not configured.
    /// </summary>
    public static string? BuiltInApiKey { get; set; }
}

/// <summary>TMDB-specific helpers over <see cref="IAppConfigService"/>.</summary>
public static class AppConfigTmdbExtensions
{
    /// <summary>
    /// Resolves the effective TMDB API key: the user-configured value if present,
    /// otherwise the built-in default (<see cref="TmdbDefaults.BuiltInApiKey"/>).
    /// Returns null only when neither a user key nor a built-in default is set.
    /// </summary>
    public static async Task<string?> GetTmdbApiKeyAsync(
        this IAppConfigService appConfig, CancellationToken ct = default)
    {
        var userKey = await appConfig.GetAsync(AppConfigKeys.TmdbApiKey, ct);
        if (!string.IsNullOrWhiteSpace(userKey)) return userKey;
        return string.IsNullOrWhiteSpace(TmdbDefaults.BuiltInApiKey) ? null : TmdbDefaults.BuiltInApiKey;
    }
}
