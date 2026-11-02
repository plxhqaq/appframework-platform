using Platform.Contracts.Webhooks;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Webhooks.Domain;

/// <summary>Webhook 订阅。按应用隔离，密钥用于 HMAC 签名。</summary>
public sealed class WebhookSubscription : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>归属应用。</summary>
    public required string AppKey { get; set; }

    /// <summary>接收地址。</summary>
    public required string Url { get; set; }

    /// <summary>订阅事件（JSON 数组；空数组表示全部事件）。</summary>
    public required string EventTypesJson { get; set; }

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>签名密钥明文（接收方需要它验签）。</summary>
    public required string Secret { get; set; }

    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }

    /// <summary>最近一次投递时间。</summary>
    public DateTimeOffset? LastDeliveryAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>投递记录。</summary>
    public ICollection<WebhookDelivery> Deliveries { get; } = [];
}

/// <summary>一次事件投递。失败按指数退避重试，达到上限后转为 dead。</summary>
public sealed class WebhookDelivery : IAuditableEntity
{
    /// <summary>主键（同时作为 <c>X-Platform-Delivery</c>）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>归属应用。</summary>
    public required string AppKey { get; set; }

    /// <summary>订阅 id。</summary>
    public Guid SubscriptionId { get; set; }

    /// <summary>事件类型。</summary>
    public required string EventType { get; set; }

    /// <summary>投递负载（完整信封 JSON）。</summary>
    public required string PayloadJson { get; set; }

    /// <summary>状态：pending/failed/succeeded/dead。</summary>
    public string Status { get; set; } = WebhookConventions.StatusPending;

    /// <summary>已尝试次数。</summary>
    public int Attempts { get; set; }

    /// <summary>下次尝试时间（终态为 null）。</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>最近尝试时间。</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>最近 HTTP 状态码。</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>最近错误。</summary>
    public string? LastError { get; set; }

    /// <summary>成功时间。</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>订阅。</summary>
    public WebhookSubscription? Subscription { get; set; }
}
