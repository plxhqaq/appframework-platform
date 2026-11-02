namespace Platform.Contracts.Webhooks;

/// <summary>Webhook 投递约定：事件白名单、签名头、状态机与重试上限。</summary>
public static class WebhookConventions
{
    /// <summary>签名密钥前缀（仅用于识别，不是凭据）。</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>单条订阅最多订阅的事件数。</summary>
    public const int MaxEventTypes = 20;

    /// <summary>默认最大投递次数（含首次）。</summary>
    public const int DefaultMaxAttempts = 8;

    /// <summary>待投递。</summary>
    public const string StatusPending = "pending";

    /// <summary>投递失败、等待重试。</summary>
    public const string StatusFailed = "failed";

    /// <summary>投递成功。</summary>
    public const string StatusSucceeded = "succeeded";

    /// <summary>已达重试上限，不再自动投递。</summary>
    public const string StatusDead = "dead";

    /// <summary>签名头：<c>sha256=hex(HMAC-SHA256(secret, timestamp.payload))</c>。</summary>
    public const string SignatureHeader = "X-Platform-Signature";

    /// <summary>Unix 秒时间戳头（与负载一起参与签名，防重放）。</summary>
    public const string TimestampHeader = "X-Platform-Timestamp";

    /// <summary>事件类型头。</summary>
    public const string EventHeader = "X-Platform-Event";

    /// <summary>投递 id 头（接收方据此幂等）。</summary>
    public const string DeliveryHeader = "X-Platform-Delivery";

    /// <summary>版本发布。</summary>
    public const string EventVersionReleased = "version.released";

    /// <summary>授权吊销。</summary>
    public const string EventLicenseRevoked = "license.revoked";

    /// <summary>备份下发成功。</summary>
    public const string EventBackupPushed = "backup.pushed";

    /// <summary>文档上传。</summary>
    public const string EventDocumentUploaded = "document.uploaded";

    /// <summary>文档删除。</summary>
    public const string EventDocumentDeleted = "document.deleted";

    /// <summary>订阅测试事件（手动触发）。</summary>
    public const string EventTest = "webhook.test";

    /// <summary>全部可订阅事件。</summary>
    public static IReadOnlyList<string> EventTypes { get; } =
    [
        EventVersionReleased,
        EventLicenseRevoked,
        EventBackupPushed,
        EventDocumentUploaded,
        EventDocumentDeleted,
        EventTest,
    ];

    /// <summary>是否为可订阅事件。</summary>
    public static bool IsKnownEvent(string? type) =>
        !string.IsNullOrWhiteSpace(type) && EventTypes.Contains(type, StringComparer.Ordinal);
}

/// <summary>创建订阅。密钥只在响应中出现一次。</summary>
/// <param name="Url">接收地址（http/https）。</param>
/// <param name="EventTypes">订阅事件；为空表示全部事件。</param>
/// <param name="Description">说明。</param>
/// <param name="IsEnabled">是否启用，默认启用。</param>
public sealed record CreateWebhookSubscriptionRequest(
    string Url,
    IReadOnlyList<string>? EventTypes = null,
    string? Description = null,
    bool? IsEnabled = null);

/// <summary>更新订阅；字段为空表示不变。</summary>
/// <param name="Url">接收地址。</param>
/// <param name="EventTypes">订阅事件；传空数组表示全部事件。</param>
/// <param name="Description">说明。</param>
/// <param name="IsEnabled">启停。</param>
public sealed record UpdateWebhookSubscriptionRequest(
    string? Url = null,
    IReadOnlyList<string>? EventTypes = null,
    string? Description = null,
    bool? IsEnabled = null);

/// <summary>订阅摘要（不含密钥）。</summary>
/// <param name="Id">订阅 id。</param>
/// <param name="AppKey">归属应用。</param>
/// <param name="Url">接收地址。</param>
/// <param name="EventTypes">订阅事件。</param>
/// <param name="Description">说明。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="SecretHint">密钥提示（明文密钥的前 14 个字符 + 省略号，见 <c>WebhookSigner.Hint</c>）。</param>
/// <param name="CreatedBy">创建人。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
/// <param name="LastDeliveryAt">最近一次投递时间。</param>
public sealed record WebhookSubscriptionDto(
    Guid Id,
    string AppKey,
    string Url,
    IReadOnlyList<string> EventTypes,
    string? Description,
    bool IsEnabled,
    string SecretHint,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastDeliveryAt);

/// <summary>创建结果：密钥只在此出现一次。</summary>
/// <param name="Subscription">订阅摘要。</param>
/// <param name="Secret">签名密钥明文（关闭后不可再查看）。</param>
public sealed record CreatedWebhookSubscriptionDto(
    WebhookSubscriptionDto Subscription,
    string Secret);

/// <summary>轮换后的新密钥。</summary>
/// <param name="Secret">签名密钥明文。</param>
public sealed record WebhookSecretRotatedDto(string Secret);

/// <summary>投递记录。</summary>
/// <param name="Id">投递 id。</param>
/// <param name="SubscriptionId">订阅 id。</param>
/// <param name="EventType">事件类型。</param>
/// <param name="Status">状态：pending/failed/succeeded/dead。</param>
/// <param name="Attempts">已尝试次数。</param>
/// <param name="NextAttemptAt">下次尝试时间。</param>
/// <param name="LastAttemptAt">最近尝试时间。</param>
/// <param name="LastStatusCode">最近 HTTP 状态码。</param>
/// <param name="LastError">最近错误。</param>
/// <param name="PayloadJson">投递负载。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="DeliveredAt">成功时间。</param>
public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid SubscriptionId,
    string EventType,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    int? LastStatusCode,
    string? LastError,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

/// <summary>手动测试结果。</summary>
/// <param name="DeliveryCount">生成的投递条数。</param>
public sealed record WebhookTestResultDto(int DeliveryCount);
