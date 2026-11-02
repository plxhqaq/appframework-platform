namespace Platform.Kernel.Abstractions;

/// <summary>
/// 平台领域事件。模块只描述"发生了什么"，订阅、签名与重投由 Webhook 模块负责。
/// </summary>
/// <param name="Type">事件类型，例如 <c>version.released</c>。</param>
/// <param name="AppKey">事件归属应用；平台级事件为 <c>null</c>。</param>
/// <param name="Data">事件负载（由发布实现序列化）。</param>
/// <param name="OccurredAt">发生时间（UTC）。</param>
public sealed record PlatformEvent(
    string Type,
    string? AppKey,
    object? Data = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// 领域事件发布。**发布失败不得阻断主业务**：Webhook 是旁路能力，
/// 订阅方不可用不应让版本发布或授权吊销失败。
/// </summary>
public interface IPlatformEventPublisher
{
    /// <summary>发布事件；无订阅时静默返回。</summary>
    Task PublishAsync(PlatformEvent platformEvent, CancellationToken cancellationToken = default);
}

/// <summary>默认空实现：Webhook 模块未装配时事件直接丢弃，其它模块无需感知。</summary>
public sealed class NullPlatformEventPublisher : IPlatformEventPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(PlatformEvent platformEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
