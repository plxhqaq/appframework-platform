using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Webhooks.Domain;
using Platform.Modules.Webhooks.Options;

namespace Platform.Modules.Webhooks.Services;

/// <summary>
/// 终态 Webhook 投递记录清理：succeeded / dead 超过保留期后删除；
/// pending / failed 属于重试队列，即使很旧也保留，避免把待重投的投递删掉。
/// </summary>
public sealed class WebhookDeliveryCleanupTask : IRetentionCleanupTask
{
    private const int BatchSize = 500;

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly WebhooksOptions _options;

    /// <summary>构造。</summary>
    public WebhookDeliveryCleanupTask(
        PlatformDbContext db,
        IClock clock,
        IOptions<WebhooksOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public string Name => "webhooks";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddDays(-Math.Max(1, _options.DeliveryRetentionDays));
        var rows = 0;
        while (true)
        {
            var batch = await _db.Set<WebhookDelivery>()
                .Where(x => (x.Status == WebhookConventions.StatusSucceeded
                             || x.Status == WebhookConventions.StatusDead)
                            && x.UpdatedAt <= cutoff)
                .OrderBy(static x => x.UpdatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return new RetentionCleanupResult(rows, 0);
            }

            _db.Set<WebhookDelivery>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            rows += batch.Count;
        }
    }
}
