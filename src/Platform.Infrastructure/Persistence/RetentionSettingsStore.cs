using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence.Entities;
using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Persistence;

/// <summary>宽限期配置读写：upsert 单次 SaveChanges，读取直查不缓存。</summary>
public sealed class RetentionSettingsStore : IRetentionSettingsStore
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public RetentionSettingsStore(PlatformDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default) =>
        _db.Set<RetentionSettingsRow>()
            .AsNoTracking()
            .Where(x => x.ModuleKey == moduleKey)
            .Select(static x => (int?)x.CleanupGraceMinutes)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(minutesByModule);

        var keys = minutesByModule.Keys.ToList();
        var existing = await _db.Set<RetentionSettingsRow>()
            .Where(x => keys.Contains(x.ModuleKey))
            .ToDictionaryAsync(static x => x.ModuleKey, StringComparer.Ordinal, cancellationToken);

        var now = _clock.UtcNow;
        foreach (var pair in minutesByModule)
        {
            if (existing.TryGetValue(pair.Key, out var row))
            {
                row.CleanupGraceMinutes = pair.Value;
                row.UpdatedAt = now;
                row.UpdatedBy = updatedBy;
            }
            else
            {
                _db.Set<RetentionSettingsRow>().Add(new RetentionSettingsRow
                {
                    ModuleKey = pair.Key,
                    CleanupGraceMinutes = pair.Value,
                    UpdatedAt = now,
                    UpdatedBy = updatedBy,
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
