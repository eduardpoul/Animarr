namespace Animarr.Web.Services;

/// <summary>
/// Provides access to application configuration stored in the database.
/// Unlike IOptions&lt;AppSettings&gt; (which is read once at startup from appsettings.json),
/// this service reads and writes live from the DB — changes take effect immediately
/// without restarting the application.
/// </summary>
public interface IAppConfigService
{
    /// <summary>Returns the raw string value for the key, or null if not set.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Returns the value parsed as <typeparamref name="T"/>, or <paramref name="defaultValue"/> if not set / not parseable.</summary>
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default);

    /// <summary>Persists a string value (upsert).</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>Persists a value of any type via its string representation (upsert).</summary>
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);

    /// <summary>Loads all keys matching the given prefix into a dictionary.</summary>
    Task<Dictionary<string, string?>> GetAllAsync(string? prefix = null, CancellationToken ct = default);
}
