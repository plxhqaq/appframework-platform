namespace Platform.Kernel.Abstractions;

/// <summary>
/// 管理台事件发布：按权限域向 <c>/hubs/admin</c> 推送提示事件，前端据此 invalidate 回拉。
/// 事件只是提示，事实以管理端接口为准；发布失败不得影响已落库的业务结果。
/// </summary>
public interface IAdminEventPublisher
{
    /// <summary>向指定权限域组发布事件（域词表见 <c>AdminHubDomains</c>）。</summary>
    Task PublishAsync(
        string domain,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);
}