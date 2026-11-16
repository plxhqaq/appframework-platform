using Platform.Contracts.Devices;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 设备命令网关：设备定位（在线/工位）、定向下发与重启。由 Devices 模块实现，
/// 其它模块只通过本接口使用，不引用 Devices 实体或 Hub。
/// </summary>
/// <remarks>
/// 业务编排（兼容矩阵、快照、跨设备确认、Webhook/审计）留在调用方；
/// 本接口只负责"目标在哪里、命令怎么送到、Ack 怎么等"。
/// </remarks>
public interface IDeviceCommandGateway
{
    /// <summary>查询目标设备；设备不存在或不属于该应用时返回 <c>null</c>。</summary>
    Task<DeviceCommandTarget?> GetTargetAsync(
        string appKey,
        Guid deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定设备下发配置：连接不存在时 <see cref="DevicePushResult.Delivered"/> = false；
    /// 已送达但超时无回执时 <see cref="DevicePushResult.AckReceived"/> = false。
    /// </summary>
    Task<DevicePushResult> PushConfigAsync(
        string appKey,
        Guid deviceId,
        DeviceConfigPush command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>发送重启命令；设备不在线时抛 <see cref="Platform.Contracts.ApiErrorCode.DevicesOffline"/>。</summary>
    Task SendRestartAsync(
        string appKey,
        Guid deviceId,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>设备命令目标快照：门禁只看这里，不读 Devices 实体。</summary>
/// <param name="DeviceId">设备主键。</param>
/// <param name="OnlineStatus">在线状态，取值见 <see cref="DeviceOnlineStatuses"/>。</param>
/// <param name="StationStatus">工位状态，取值见 <see cref="DeviceStationStatuses"/>。</param>
/// <param name="Branch">版本分支（渠道镜像）。</param>
/// <param name="FrameworkVersion">客户端框架版本。</param>
/// <param name="MachineCode">机器码（日志与人工核对）。</param>
/// <param name="IsEnabled">是否启用；停用设备不接受命令。</param>
public sealed record DeviceCommandTarget(
    Guid DeviceId,
    string OnlineStatus,
    string StationStatus,
    string? Branch,
    string? FrameworkVersion,
    string? MachineCode,
    bool IsEnabled);

/// <summary>设备下发结果。</summary>
/// <param name="Delivered">是否送达（设备连接存在且已发出）。</param>
/// <param name="AckReceived">是否收到设备回执。</param>
/// <param name="Succeeded">回执内容：设备是否应用成功。</param>
/// <param name="Message">设备回执说明。</param>
public sealed record DevicePushResult(
    bool Delivered,
    bool AckReceived,
    bool Succeeded,
    string? Message);
