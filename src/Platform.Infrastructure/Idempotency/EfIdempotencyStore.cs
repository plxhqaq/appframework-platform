using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Idempotency;

/// <summary>数据库幂等存储：依赖 <c>(scope,key)</c> 唯一索引保证多副本下只有一个赢家。</summary>
public sealed class EfIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public EfIdempotencyStore(PlatformDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveAsync(
        string scope,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _clock.UtcNow;
        var existing = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.Scope == scope && r.Key == key,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.ExpiresAt > now)
            {
                return false;
            }

            // 过期记录允许同键重新执行；先删再插，并发的唯一索引冲突仍然兜底为 false。
            _db.IdempotencyRecords.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Scope = scope,
            Key = key,
            Status = IdempotencyRecord.StatusReserved,
            ExpiresAt = now.Add(ttl),
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // 唯一索引冲突：另一个并发请求已经占位。上下文可能处于不可用状态，
            // 调用方不应继续用同一个 DbContext 写业务数据。
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var record = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Scope == scope && r.Key == key
                     && r.Status == IdempotencyRecord.StatusCompleted,
                cancellationToken);

        if (record?.ResponseJson is null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(record.ResponseJson, JsonOptions);
    }

    /// <inheritdoc />
    public async Task SaveAsync<T>(
        string scope,
        string key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _clock.UtcNow;
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.Scope == scope && r.Key == key,
                cancellationToken);

        if (record is null)
        {
            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Scope = scope,
                Key = key,
                Status = IdempotencyRecord.StatusCompleted,
                ResponseJson = json,
                ExpiresAt = now.Add(ttl),
            });
        }
        else
        {
            record.Status = IdempotencyRecord.StatusCompleted;
            record.ResponseJson = json;
            record.ExpiresAt = now.Add(ttl);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // 只释放“处理中”的占位；已完成的结果必须保留给后续重复请求回放。
        var reserved = await _db.IdempotencyRecords
            .Where(r => r.Scope == scope && r.Key == key
                        && r.Status == IdempotencyRecord.StatusReserved)
            .ToListAsync(cancellationToken);

        if (reserved.Count == 0)
        {
            return;
        }

        _db.IdempotencyRecords.RemoveRange(reserved);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
