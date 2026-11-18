using Platform.Contracts.Devices;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 安装实例命令网关：定位实例/节点（在线/工位）、定向下发与重启。由 Devices 模块实现，
/// 其它模块只通过本接口使用，不引用 Devices 实体或 Hub。
/// </summary>
/// <remarks>
/// 业务编排（兼容矩阵、快照、跨设备确认、Webhook/审计）留在调用方；
/// 本接口只负责"目标在哪里、命令怎么送到、Ack 怎么等"。
/// </remarks>
public interface IInstallationCommandGateway
{
    /// <summary>查询目标安装实例；不存在或不属于该应用时返回 <c>null</c>。</summary>
    Task<InstallationCommandTarget?> GetTargetAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定安装实例下发配置：连接不存在时 <see cref="InstallationPushResult.Delivered"/> = false；
    /// 已送达但超时无回执时 <see cref="InstallationPushResult.AckReceived"/> = false。
    /// </summary>
    Task<InstallationPushResult> PushConfigAsync(
        string appKey,
        Guid installationId,
        DeviceConfigPush command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>发送重启命令；安装实例不在线时抛 <see cref="Platform.Contracts.ApiErrorCode.DevicesOffline"/>。</summary>
    Task SendRestartAsync(
        string appKey,
        Guid installationId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>向节点全部在线安装实例发送重启；返回送达的连接数。</summary>
    Task<int> SendNodeRestartAsync(
        Guid nodeId,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>安装实例命令目标快照：门禁只看这里，不读 Devices 实体。</summary>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="NodeId">物理节点主键。</param>
/// <param name="OnlineStatus">在线状态，取值见 <see cref="DeviceOnlineStatuses"/>。</param>
/// <param name="StationStatus">工位状态，取值见 <see cref="DeviceStationStatuses"/>。</param>
/// <param name="Branch">版本分支（渠道镜像）。</param>
/// <param name="FrameworkVersion">客户端框架版本。</param>
/// <param name="MachineCode">机器码（日志与人工核对）。</param>
/// <param name="IsEnabled">安装实例开关。</param>
/// <param name="NodeEnabled">节点开关；任一为假都不接受命令。</param>
public sealed record InstallationCommandTarget(
    Guid InstallationId,
    Guid NodeId,
    string OnlineStatus,
    string StationStatus,
    string? Branch,
    string? FrameworkVersion,
    string? MachineCode,
    bool IsEnabled,
    bool NodeEnabled);

/// <summary>安装实例下发结果。</summary>
/// <param name="Delivered">是否送达（连接存在且已发出）。</param>
/// <param name="AckReceived">是否收到安装实例回执。</param>
/// <param name="Succeeded">回执内容：安装实例是否应用成功。</param>
/// <param name="Message">安装实例回执说明。</param>
public sealed record InstallationPushResult(
    bool Delivered,
    bool AckReceived,
    bool Succeeded,
    string? Message);
