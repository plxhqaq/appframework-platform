using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Webhooks.Domain;

namespace Platform.Modules.Webhooks.Services;

/// <summary>
/// 领域事件 → Webhook 投递行。
/// </summary>
/// <remarks>
/// 发布是旁路：任何异常只记日志，不能把版本发布/授权吊销等主业务拖垮。
/// </remarks>
public sealed class WebhookEventPublisher : IPlatformEventPublisher
{
    private static readonly Action<ILogger, string, string?, Exception?> PublishFailed =
        LoggerMessage.Define<string, string?>(
            LogLevel.Error,
            new EventId(1, "WebhookPublishFailed"),
            "Webhook 事件落库失败：type={Type} app={AppKey}");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<WebhookEventPublisher> _logger;

    /// <summary>构造。</summary>
    public WebhookEventPublisher(
        PlatformDbContext db,
        IClock clock,
        ILogger<WebhookEventPublisher> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(PlatformEvent platformEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platformEvent);

        // Webhook 订阅按应用隔离；没有应用上下文的平台事件不投递。
        if (string.IsNullOrWhiteSpace(platformEvent.AppKey))
        {
            return;
        }

        try
        {
            var subscriptions = await _db.Set<WebhookSubscription>()
                .Where(x => x.AppKey == platformEvent.AppKey && x.IsEnabled)
                .ToListAsync(cancellationToken);
            if (subscriptions.Count == 0)
            {
                return;
            }

            var occurredAt = platformEvent.OccurredAt ?? _clock.UtcNow;
            var created = 0;
            foreach (var subscription in subscriptions)
            {
                var events = WebhookSubscriptionService.ParseEvents(subscription.EventTypesJson);
                if (events.Count > 0 && !events.Contains(platformEvent.Type, StringComparer.Ordinal))
                {
                    continue;
                }

                var delivery = new WebhookDelivery
                {
                    AppKey = platformEvent.AppKey,
                    SubscriptionId = subscription.Id,
                    EventType = platformEvent.Type,
                    PayloadJson = string.Empty,
                    Status = WebhookConventions.StatusPending,
                    NextAttemptAt = occurredAt,
                };
                delivery.PayloadJson = WebhookSigner.BuildPayload(
                    delivery.Id,
                    delivery.EventType,
                    platformEvent.AppKey,
                    occurredAt,
                    platformEvent.Data);

                _db.Set<WebhookDelivery>().Add(delivery);
                created++;
            }

            if (created > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PublishFailed(_logger, platformEvent.Type, platformEvent.AppKey, exception);
        }
    }
}
