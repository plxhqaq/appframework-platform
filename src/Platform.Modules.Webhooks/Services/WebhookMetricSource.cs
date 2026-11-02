using Microsoft.EntityFrameworkCore;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Webhooks.Domain;

namespace Platform.Modules.Webhooks.Services;

/// <summary>
/// Webhook 积压仪表：按应用与状态统计投递，并给出最久未成功的等待时长。
/// </summary>
/// <remarks>
/// 只聚合、不遍历：<c>ix_webhook_deliveries_app_created</c> 与状态索引支撑这两条分组查询。
/// 仪表而非计数器，是因为「现在还有多少条没投出去」才是告警要的值，
/// 累计次数只能靠 rate() 推断，重启即清零。
/// </remarks>
public sealed class WebhookMetricSource(PlatformDbContext db, IClock clock)
    : IPlatformMetricSource
{
    private const string BacklogGauge = "platform_webhook_deliveries";
    private const string BacklogGaugeHelp = "Webhook 投递数量，按应用与状态（pending/failed/succeeded/dead）分组。";
    private const string OldestPendingGauge = "platform_webhook_oldest_pending_age_seconds";
    private const string OldestPendingGaugeHelp = "最久一条未成功投递（pending/failed）的等待秒数。";

    /// <inheritdoc />
    public string Name => "webhooks";

    /// <inheritdoc />
    public async Task CollectAsync(PlatformMetrics metrics, CancellationToken cancellationToken = default)
    {
        var grouped = await db.Set<WebhookDelivery>()
            .GroupBy(static x => new { x.AppKey, x.Status })
            .Select(static g => new { g.Key.AppKey, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // 抓取时清零再写：投递成功后状态会迁移，残留的旧值会让告警误判为持续积压。
        foreach (var item in grouped)
        {
            metrics.Set(
                BacklogGauge,
                BacklogGaugeHelp,
                item.Count,
                new Dictionary<string, string> { ["app"] = item.AppKey, ["status"] = item.Status });
        }

        var oldest = await db.Set<WebhookDelivery>()
            .Where(static x => x.Status == WebhookConventions.StatusPending || x.Status == WebhookConventions.StatusFailed)
            .GroupBy(static x => x.AppKey)
            .Select(static g => new { AppKey = g.Key, Oldest = g.Min(static x => x.CreatedAt) })
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        foreach (var item in oldest)
        {
            metrics.Set(
                OldestPendingGauge,
                OldestPendingGaugeHelp,
                Math.Max(0, (now - item.Oldest).TotalSeconds),
                new Dictionary<string, string> { ["app"] = item.AppKey });
        }
    }
}
