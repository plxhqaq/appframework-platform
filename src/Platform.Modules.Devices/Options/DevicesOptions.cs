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
}
