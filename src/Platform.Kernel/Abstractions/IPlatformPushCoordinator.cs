namespace Platform.Kernel.Abstractions;

/// <summary>
/// 配置下发回执协调器：Hub 维护在线连接，下发端点等待目标回执。
/// 实现为进程内单例；多实例部署时应替换为带背板的实现（Redis 等）。
/// </summary>
public interface IPlatformPushCoordinator
{
    /// <summary>连接建立：登记连接所属应用。</summary>
    void RegisterConnection(string connectionId, string appKey);

    /// <summary>连接断开：注销连接。</summary>
    void UnregisterConnection(string connectionId);

    /// <summary>应用当前在线连接数。</summary>
    int ConnectionCount(string appKey);

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
