namespace Platform.Kernel.Abstractions;

/// <summary>
/// 应用组广播回执协调器：下发端点等待（应用，备份）配对的首个回执。
/// 连接在线事实由 Devices 模块的连接登记表提供，这里只管回执等待。
/// 实现为进程内单例；多实例部署时应替换为带背板的实现（Redis 等）。
/// </summary>
public interface IPlatformPushCoordinator
{
    /// <summary>收到回执：唤醒等待方（同一备份只认第一个回执）。</summary>
    void CompleteAck(string appKey, Guid backupId, bool succeeded, string? message);

    /// <summary>
    /// 开始等待回执。**必须在发送推送之前调用**（先注册再发送，避免回执早到丢失）；
    /// 超时返回 <see cref="PushAckResult.Received"/> = false，不抛异常。
    /// </summary>
    Task<PushAckResult> WaitForAckAsync(
        string appKey,
        Guid backupId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>下发回执结果。</summary>
/// <param name="Received">是否收到回执。</param>
/// <param name="Succeeded">回执内容：客户端是否应用成功。</param>
/// <param name="Message">客户端回执说明。</param>
public readonly record struct PushAckResult(bool Received, bool Succeeded, string? Message);