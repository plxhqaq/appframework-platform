using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Devices.Domain;

/// <summary>
/// 设备（一台物理机）。按 <see cref="AppKey"/> 隔离、按 <see cref="MachineCode"/> 去重：
/// 同一台机器上的多个客户端安装共用一条记录；在线/工位状态都存字符串词表，平台不落旧端数字枚举。
/// </summary>
public sealed class Device : IAuditableEntity
{
    /// <summary>主键（服务端分配，重连稳定）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>应用隔离轴（来自令牌，不由客户端传参）。</summary>
    public required string AppKey { get; set; }

    /// <summary>设备键（等于机器码，应用内唯一）；短 ID 与历史接口兼容保留。</summary>
    public required string DeviceKey { get; set; }

    /// <summary>机器码（16 字符 Crockford Base32），授权、去重与管理端检索用。</summary>
    public required string MachineCode { get; set; }

    /// <summary>最近一次注册的安装 Guid（同一设备可能运行多个客户端安装）。</summary>
    public Guid? InstallGuid { get; set; }

    /// <summary>已合并到的主设备；非空表示该行是历史重复记录，只保留供审计。</summary>
    public Guid? MergedIntoDeviceId { get; set; }

    /// <summary>自动设备名（客户端机器名）。</summary>
    public string? AutoName { get; set; }

    /// <summary>站点自定义名。</summary>
    public string? CustomName { get; set; }

    /// <summary>展示名：自定义名 + 自动名。</summary>
    public required string DisplayName { get; set; }

    /// <summary>最近注册 IP。</summary>
    public string? IpAddress { get; set; }

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

    /// <summary>是否启用；停用后拒绝注册/心跳，管理端仍可查看。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
