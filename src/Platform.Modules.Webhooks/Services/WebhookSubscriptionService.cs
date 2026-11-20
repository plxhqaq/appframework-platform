using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Webhooks.Domain;

namespace Platform.Modules.Webhooks.Services;

/// <summary>Webhook 订阅与投递记录读写。</summary>
public interface IWebhookSubscriptionService
{
    /// <summary>列出当前应用的订阅。</summary>
    Task<IReadOnlyList<WebhookSubscriptionDto>> ListAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>创建订阅；密钥只在返回值中出现一次。</summary>
    Task<CreatedWebhookSubscriptionDto> CreateAsync(
        string appKey,
        CreateWebhookSubscriptionRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>更新订阅。</summary>
    Task<WebhookSubscriptionDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除订阅及其投递记录；启用中或存在待投递/待重试记录时拒绝。</summary>
    Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>轮换签名密钥。</summary>
    Task<WebhookSecretRotatedDto> RotateSecretAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>分页查询投递记录。</summary>
    Task<PageResult<WebhookDeliveryDto>> ListDeliveriesAsync(
        string appKey,
        Guid? subscriptionId,
        PageQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>手动重投失败/终止的投递。</summary>
    Task<WebhookDeliveryDto> RedeliverAsync(
        string appKey,
        Guid deliveryId,
        CancellationToken cancellationToken = default);

    /// <summary>向订阅发送一条测试事件。</summary>
    Task<WebhookTestResultDto> TestAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>订阅服务实现。</summary>
public sealed class WebhookSubscriptionService : IWebhookSubscriptionService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly WebhookUrlPolicy _urlPolicy;

    /// <summary>构造。</summary>
    public WebhookSubscriptionService(PlatformDbContext db, IClock clock, WebhookUrlPolicy urlPolicy)
    {
        _db = db;
        _clock = clock;
        _urlPolicy = urlPolicy;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookSubscriptionDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<WebhookSubscription>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    /// <inheritdoc />
    public async Task<CreatedWebhookSubscriptionDto> CreateAsync(
        string appKey,
        CreateWebhookSubscriptionRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = await _urlPolicy.ValidateAsync(request.Url, cancellationToken);
        var eventTypes = NormalizeEventTypes(request.EventTypes);
        var secret = WebhookSigner.GenerateSecret();

        var entity = new WebhookSubscription
        {
            AppKey = appKey,
            Url = url,
            EventTypesJson = SerializeEvents(eventTypes),
            Description = TrimTo(request.Description, 1024),
            IsEnabled = request.IsEnabled ?? true,
            Secret = secret,
            CreatedBy = actor,
        };

        _db.Set<WebhookSubscription>().Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatedWebhookSubscriptionDto(ToDto(entity), secret);
    }

    /// <inheritdoc />
    public async Task<WebhookSubscriptionDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await GetAsync(appKey, id, cancellationToken);
        if (request.Url is not null)
        {
            entity.Url = await _urlPolicy.ValidateAsync(request.Url, cancellationToken);
        }

        if (request.EventTypes is not null)
        {
            entity.EventTypesJson = SerializeEvents(NormalizeEventTypes(request.EventTypes));
        }

        if (request.Description is not null)
        {
            entity.Description = TrimTo(request.Description, 1024);
        }

        if (request.IsEnabled is { } isEnabled)
        {
            entity.IsEnabled = isEnabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await GetAsync(appKey, id, cancellationToken);
        if (entity.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                "订阅正在启用，请先停用再删除。");
        }

        // pending/failed 仍会被 dispatcher 重试，删除会让在途投递丢失；先等其终结（成功或 dead）。
        var hasInFlight = await _db.Set<WebhookDelivery>()
            .AnyAsync(
                x => x.SubscriptionId == id
                     && (x.Status == WebhookConventions.StatusPending
                         || x.Status == WebhookConventions.StatusFailed),
                cancellationToken);
        if (hasInFlight)
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                "该订阅仍有待投递或等待重试的记录，请处理完再删除。");
        }

        var deliveries = await _db.Set<WebhookDelivery>()
            .Where(x => x.SubscriptionId == id)
            .ToListAsync(cancellationToken);
        _db.Set<WebhookDelivery>().RemoveRange(deliveries);
        _db.Set<WebhookSubscription>().Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WebhookSecretRotatedDto> RotateSecretAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await GetAsync(appKey, id, cancellationToken);
        var secret = WebhookSigner.GenerateSecret();
        entity.Secret = secret;
        await _db.SaveChangesAsync(cancellationToken);
        return new WebhookSecretRotatedDto(secret);
    }

    /// <inheritdoc />
    public async Task<PageResult<WebhookDeliveryDto>> ListDeliveriesAsync(
        string appKey,
        Guid? subscriptionId,
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey);
        if (subscriptionId is { } id)
        {
            source = source.Where(x => x.SubscriptionId == id);
        }

        var total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.CreatedAt)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .ToListAsync(cancellationToken);

        return PageResult<WebhookDeliveryDto>.Create([.. rows.Select(ToDto)], total, query);
    }

    /// <inheritdoc />
    public async Task<WebhookDeliveryDto> RedeliverAsync(
        string appKey,
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        var delivery = await _db.Set<WebhookDelivery>()
                           .FirstOrDefaultAsync(x => x.Id == deliveryId && x.AppKey == appKey, cancellationToken)
                       ?? throw new BusinessException(
                           ApiErrorCode.WebhooksDeliveryNotFound,
                           "投递记录不存在。");

        if (delivery.Status is not (WebhookConventions.StatusFailed or WebhookConventions.StatusDead))
        {
            throw new BusinessException(
                ApiErrorCode.WebhooksDeliveryNotRetryable,
                "只有失败或已终止的投递可以手动重投。");
        }

        delivery.Status = WebhookConventions.StatusPending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = _clock.UtcNow;
        delivery.LastError = null;
        delivery.LastStatusCode = null;
        delivery.DeliveredAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(delivery);
    }

    /// <inheritdoc />
    public async Task<WebhookTestResultDto> TestAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var subscription = await GetAsync(appKey, id, cancellationToken);
        var delivery = new WebhookDelivery
        {
            AppKey = appKey,
            SubscriptionId = subscription.Id,
            EventType = WebhookConventions.EventTest,
            PayloadJson = string.Empty,
            Status = WebhookConventions.StatusPending,
            NextAttemptAt = _clock.UtcNow,
        };

        // 信封里的 id 必须与投递 id 一致：先落 id 再序列化，接收方才能用头与体互相对照。
        delivery.PayloadJson = WebhookSigner.BuildPayload(
            delivery.Id,
            delivery.EventType,
            appKey,
            _clock.UtcNow,
            new { message = "这是一条 Webhook 测试事件。" });

        _db.Set<WebhookDelivery>().Add(delivery);
        await _db.SaveChangesAsync(cancellationToken);
        return new WebhookTestResultDto(1);
    }

    private async Task<WebhookSubscription> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken) =>
        await _db.Set<WebhookSubscription>()
            .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
        ?? throw new BusinessException(ApiErrorCode.WebhooksSubscriptionNotFound, "Webhook 订阅不存在。");

    internal static IReadOnlyList<string> ParseEvents(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static WebhookSubscriptionDto ToDto(WebhookSubscription entity) =>
        new(
            entity.Id,
            entity.AppKey,
            entity.Url,
            ParseEvents(entity.EventTypesJson),
            entity.Description,
            entity.IsEnabled,
            WebhookSigner.Hint(entity.Secret),
            entity.CreatedBy,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.LastDeliveryAt);

    internal static WebhookDeliveryDto ToDto(WebhookDelivery entity) =>
        new(
            entity.Id,
            entity.SubscriptionId,
            entity.EventType,
            entity.Status,
            entity.Attempts,
            entity.NextAttemptAt,
            entity.LastAttemptAt,
            entity.LastStatusCode,
            entity.LastError,
            entity.PayloadJson,
            entity.CreatedAt,
            entity.DeliveredAt);

    internal static string SerializeEvents(IReadOnlyList<string> eventTypes) =>
        JsonSerializer.Serialize(eventTypes, JsonOptions);

    internal static IReadOnlyList<string> NormalizeEventTypes(IReadOnlyList<string>? eventTypes)
    {
        if (eventTypes is null || eventTypes.Count == 0)
        {
            return [];
        }

        var distinct = eventTypes
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count > WebhookConventions.MaxEventTypes)
        {
            throw BusinessException.Invalid($"单条订阅最多订阅 {WebhookConventions.MaxEventTypes} 个事件。");
        }

        var unknown = distinct.FirstOrDefault(static x => !WebhookConventions.IsKnownEvent(x));
        if (unknown is not null)
        {
            throw new BusinessException(ApiErrorCode.WebhooksEventUnknown, $"未知事件类型：{unknown}。");
        }

        return distinct;
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
