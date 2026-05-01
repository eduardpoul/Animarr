using System.Globalization;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

public class AppConfigService(IDbContextFactory<AppDbContext> dbFactory) : IAppConfigService
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppConfigs.FindAsync([key], ct);
        return row?.Value;
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppConfigs.FindAsync([key], ct);
        if (row is null)
        {
            db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var str = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return SetAsync(key, str, ct);
    }

    public async Task<Dictionary<string, string?>> GetAllAsync(string? prefix = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.AppConfigs.AsQueryable();
        if (prefix is not null)
            query = query.Where(c => c.Key.StartsWith(prefix));
        return await query.ToDictionaryAsync(c => c.Key, c => c.Value, ct);
    }
}
