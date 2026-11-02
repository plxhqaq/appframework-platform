using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Webhooks.Domain;
using Platform.Modules.Webhooks.Options;

namespace Platform.Modules.Webhooks.Services;

/// <summary>扫描到期投递并发送（由后台 Worker 与测试直接调用）。</summary>
public interface IWebhookDispatcher
{
    /// <summary>处理至多 <paramref name="limit"/> 条到期投递，返回处理条数。</summary>
    Task<int> DispatchDueAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>投递调度实现：成功落 succeeded，失败按指数退避重排，超限落 dead。</summary>
public sealed class WebhookDispatcher : IWebhookDispatcher
{
    private static readonly Action<ILogger, Guid, string, int, Exception?> DeliveryAttempted =
        LoggerMessage.Define<Guid, string, int>(
            LogLevel.Debug,
            new EventId(1, "WebhookDeliveryAttempted"),
            "Webhook 投递完成：delivery={DeliveryId} status={Status} attempts={Attempts}");

    /// <summary>投递结果计数器：dead 增长就是要告警的信号。</summary>
    private const string DeliveryCounter = "platform_webhook_deliveries_total";
    private const string DeliveryCounterHelp = "Webhook 投递结果累计次数，按结果分类（success/retry/dead）。";

    private readonly PlatformDbContext _db;
    private readonly IWebhookSender _sender;
    private readonly IClock _clock;
    private readonly PlatformMetrics _metrics;
    private readonly WebhooksOptions _options;
    private readonly ILogger<WebhookDispatcher> _logger;

    /// <summary>构造。</summary>
    public WebhookDispatcher(
        PlatformDbContext db,
        IWebhookSender sender,
        IClock clock,
        PlatformMetrics metrics,
        IOptions<WebhooksOptions> options,
        ILogger<WebhookDispatcher> logger)
    {
        _db = db;
        _sender = sender;
        _clock = clock;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> DispatchDueAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var due = await _db.Set<WebhookDelivery>()
            .Where(x => (x.Status == WebhookConventions.StatusPending || x.Status == WebhookConventions.StatusFailed)
                        && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(static x => x.NextAttemptAt)
            .ThenBy(static x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var subscriptions = await _db.Set<WebhookSubscription>()
            .Where(x => due.Select(static d => d.SubscriptionId).Contains(x.Id))
            .ToDictionaryAsync(static x => x.Id, cancellationToken);

        var processed = 0;
        foreach (var delivery in due)
        {
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription)
                || !subscription.IsEnabled)
            {
                delivery.Status = WebhookConventions.StatusDead;
                delivery.NextAttemptAt = null;
                delivery.LastError = "订阅不存在或已停用。";
                await _db.SaveChangesAsync(cancellationToken);
                Count(delivery, "dead");
                processed++;
                continue;
            }

            WebhookSendResult result;
            var timestamp = now.ToUnixTimeSeconds();
            try
            {
                result = await _sender.SendAsync(
                    subscription.Url,
                    subscription.Secret,
                    delivery.Id,
                    delivery.EventType,
                    timestamp,
                    delivery.PayloadJson,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new WebhookSendResult(false, null, exception.Message);
            }

            delivery.Attempts++;
            delivery.LastAttemptAt = now;
            delivery.LastStatusCode = result.StatusCode;
            if (result.Success)
            {
                delivery.Status = WebhookConventions.StatusSucceeded;
                delivery.DeliveredAt = now;
                delivery.NextAttemptAt = null;
                delivery.LastError = null;
                subscription.LastDeliveryAt = now;
            }
            else if (delivery.Attempts >= _options.EffectiveMaxAttempts)
            {
                delivery.Status = WebhookConventions.StatusDead;
                delivery.NextAttemptAt = null;
                delivery.LastError = result.Error;
            }
            else
            {
                delivery.Status = WebhookConventions.StatusFailed;
                delivery.NextAttemptAt = now.AddSeconds(_options.DelaySeconds(delivery.Attempts));
                delivery.LastError = result.Error;
            }

            await _db.SaveChangesAsync(cancellationToken);
            DeliveryAttempted(_logger, delivery.Id, delivery.Status, delivery.Attempts, null);
            Count(delivery, delivery.Status switch
            {
                WebhookConventions.StatusSucceeded => "success",
                WebhookConventions.StatusDead => "dead",
                _ => "retry",
            });
            processed++;
        }

        return processed;
    }

    private void Count(WebhookDelivery delivery, string result) => _metrics.Increment(
        DeliveryCounter,
        DeliveryCounterHelp,
        new Dictionary<string, string> { ["app"] = delivery.AppKey, ["event"] = delivery.EventType, ["result"] = result });
}
