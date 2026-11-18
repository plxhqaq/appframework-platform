using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Devices.Domain;

/// <summary>
/// 应用安装实例：某个应用（<see cref="AppKey"/>）在某节点上的一次客户端安装。
/// 在线/工位/报警按安装实例独立维护，互不覆盖；重装产生新行，旧行保留供审计与备份追溯。
/// </summary>
public sealed class DeviceInstallation : IAuditableEntity
{
    /// <summary>主键（服务端分配，同应用同安装重连保持稳定）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属物理节点。</summary>
    public Guid NodeId { get; set; }

    /// <summary>所属物理节点导航。</summary>
    public DeviceNode Node { get; set; } = null!;

    /// <summary>应用隔离轴（来自 JWT 的 app 声明，不由客户端传参）。</summary>
    public required string AppKey { get; set; }

    /// <summary>客户端首次安装生成并持久化的 Guid。</summary>
    public Guid InstallGuid { get; set; }

    /// <summary>版本分支。</summary>
    public string? Branch { get; set; }

    /// <summary>客户端框架版本。</summary>
    public string? FrameworkVersion { get; set; }

    /// <summary>在线状态，取值见 <see cref="DeviceOnlineStatuses"/>。</summary>
    public string OnlineStatus { get; set; } = DeviceOnlineStatuses.Offline;

    /// <summary>工位状态，取值见 <see cref="DeviceStationStatuses"/>。</summary>
    public string StationStatus { get; set; } = DeviceStationStatuses.Idle;

    /// <summary>报警级别，取值见 <see cref="DeviceAlarmLevels"/>。</summary>
    public string AlarmLevel { get; set; } = DeviceAlarmLevels.None;

    /// <summary>报警摘要。</summary>
    public string? AlarmMessage { get; set; }

    /// <summary>首次连接时间。</summary>
    public DateTimeOffset? FirstConnectedAt { get; set; }

    /// <summary>最近连接登记时间。</summary>
    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>最近心跳时间（服务端时间，离线扫描依据）。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>客户端上报的心跳周期（秒）。</summary>
    public int HeartbeatSeconds { get; set; } = DeviceConventions.DefaultHeartbeatSeconds;

    /// <summary>安装实例开关；与节点开关同时为真才有效。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
