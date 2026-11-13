using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>
/// 身份过期数据清理：刷新令牌、单次密钥、扫码会话与登录节流。
/// 只删「已失效且超过保留期」的行；锁定中的节流记录即使窗口很旧也保留。
/// </summary>
public sealed class IdentityCleanupTask : IRetentionCleanupTask
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly IdentityCleanupOptions _cleanup;
    private readonly LoginThrottleOptions _throttle;

    /// <summary>构造。</summary>
    public IdentityCleanupTask(
        PlatformDbContext db,
        IClock clock,
        IOptions<IdentityCleanupOptions> cleanup,
        IOptions<LoginThrottleOptions> throttle)
    {
        _db = db;
        _clock = clock;
        _cleanup = cleanup.Value;
        _throttle = throttle.Value;
    }

    /// <inheritdoc />
    public string Name => "identity";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var batchSize = Math.Max(1, _cleanup.BatchSize);

        var refreshCutoff = now.AddDays(-Math.Max(1, _cleanup.RefreshTokenRetentionDays));
        var singleKeyCutoff = now.AddHours(-Math.Max(1, _cleanup.SingleKeyRetentionHours));
        var qrCutoff = now.AddHours(-Math.Max(1, _cleanup.QrSessionRetentionHours));
        var throttleCutoff = now.AddMinutes(
            -(Math.Max(1, _throttle.TrackingWindowMinutes) + (Math.Max(1, _cleanup.LoginThrottleRetentionHours) * 60)));

        var rows = await DeleteBatchesAsync(
            _db.Set<RefreshToken>()
                .Where(x => x.ExpiresAt <= refreshCutoff)
                .OrderBy(static x => x.ExpiresAt),
            batchSize,
            cancellationToken);

        rows += await DeleteBatchesAsync(
            _db.Set<SingleLoginKey>()
                .Where(x => x.ExpiresAt <= singleKeyCutoff)
                .OrderBy(static x => x.ExpiresAt),
            batchSize,
            cancellationToken);

        rows += await DeleteBatchesAsync(
            _db.Set<QrLoginSession>()
                .Where(x => x.ExpiresAt <= qrCutoff)
                .OrderBy(static x => x.ExpiresAt),
            batchSize,
            cancellationToken);

        rows += await DeleteBatchesAsync(
            _db.Set<LoginThrottle>()
                .Where(x => x.WindowStart <= throttleCutoff
                            && (x.LockedUntil == null || x.LockedUntil <= now))
                .OrderBy(static x => x.WindowStart),
            batchSize,
            cancellationToken);

        return new RetentionCleanupResult(rows, 0);
    }

    private async Task<int> DeleteBatchesAsync<T>(
        IQueryable<T> query,
        int batchSize,
        CancellationToken cancellationToken)
        where T : class
    {
        var rows = 0;
        while (true)
        {
            var batch = await query.Take(batchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return rows;
            }

            _db.Set<T>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            rows += batch.Count;
        }
    }
}
