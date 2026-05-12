using System.Collections.Concurrent;
using System.Globalization;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// H-3: backed by an in-memory cache populated on first access. Cache is shared
/// across DI scopes (static-ish), since AppConfig is a single-user key/value store
/// — invalidated lazily inside <see cref="SetAsync(string,string?,CancellationToken)"/>.
/// H-10: secret keys (TMDB / MAL / LLM API keys) are stored encrypted via
/// <see cref="SecretProtector"/>. Cache holds plaintext for fast access.
/// </summary>
public class AppConfigService(
    IDbContextFactory<AppDbContext> dbFactory,
    SecretProtector protector) : IAppConfigService
{
    // Static cache: process-wide. AppConfig is a global key/value store for one user,
    // so contention is negligible and we get the win of zero round-trips on hot keys.
    private static readonly ConcurrentDictionary<string, string?> _cache = new();
    private static readonly SemaphoreSlim _loadLock = new(1, 1);
    private static bool _loaded;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var all = await db.AppConfigs.ToListAsync(ct);
            foreach (var row in all)
            {
                var v = SecretProtector.IsSecret(row.Key)
                    ? protector.TryDecrypt(row.Value)
                    : row.Value;
                _cache[row.Key] = v;
            }
            _loaded = true;
        }
        finally { _loadLock.Release(); }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _cache.TryGetValue(key, out var v) ? v : null;
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        if (raw is null) return defaultValue;
        try
        {
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        // Encrypt secrets before persisting; the cache always holds plaintext.
        string? stored = value;
        if (SecretProtector.IsSecret(key) && !string.IsNullOrEmpty(value))
            stored = protector.Encrypt(value);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppConfigs.FindAsync([key], ct);
        if (row is null)
        {
            db.AppConfigs.Add(new AppConfig { Key = key, Value = stored });
        }
        else
        {
            row.Value = stored;
        }
        await db.SaveChangesAsync(ct);
        _cache[key] = value;
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var str = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return SetAsync(key, str, ct);
    }

    public async Task<Dictionary<string, string?>> GetAllAsync(string? prefix = null, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (prefix is null)
            return new Dictionary<string, string?>(_cache);
        return _cache
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
