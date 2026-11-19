using Platform.Contracts.Devices;

namespace Platform.Modules.Devices.Options;

/// <summary>设备域配置。</summary>
public sealed class DevicesOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Devices";

    /// <summary>离线扫描周期（秒）。</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>客户端未上报心跳周期时的默认值（秒）。</summary>
    public int DefaultHeartbeatSeconds { get; set; } = DeviceConventions.DefaultHeartbeatSeconds;

    /// <summary>离线阈值倍数：阈值 = max(<see cref="OfflineMultiplier"/> × 心跳周期, <see cref="MinimumOfflineSeconds"/>)。</summary>
    public int OfflineMultiplier { get; set; } = 3;

    /// <summary>离线阈值下限（秒），避免心跳周期极小导致误判。</summary>
    public int MinimumOfflineSeconds { get; set; } = 60;

    /// <summary>是否启用后台离线扫描；集成测试可关闭以获得确定性。</summary>
    public bool OfflineScanEnabled { get; set; } = true;

    /// <summary>心跳内存态批量回库周期（秒）：心跳时间只写内存，由回写循环按此周期单条 SQL 落库。</summary>
    public int HeartbeatFlushSeconds { get; set; } = 30;

    /// <summary>是否启用心跳批量回写；关闭时每次心跳逐条落库（回退旧行为，集成测试用）。</summary>
    public bool HeartbeatFlushEnabled { get; set; } = true;

    /// <summary>心跳缓存条目 TTL（秒）：超时后下次心跳回源查库重播（含启用位与显示名）；0 表示不过期。</summary>
    public int HeartbeatCacheTtlSeconds { get; set; } = 600;
}
