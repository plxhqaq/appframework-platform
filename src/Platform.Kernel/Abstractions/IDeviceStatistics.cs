namespace Platform.Kernel.Abstractions;

/// <summary>
/// 设备统计：平台仪表盘等跨模块视图只读聚合数，不引用 Devices 实体。
/// 由 Devices 模块实现；模块缺席时调用方按全零处理。
/// </summary>
public interface IDeviceStatistics
{
    /// <summary>读取全平台设备统计（不区分应用）。</summary>
    Task<DeviceStatisticsSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>设备统计快照。</summary>
/// <param name="Total">设备总数。</param>
/// <param name="Online">数据库在线设备数。</param>
/// <param name="Idle">在线且待机设备数。</param>
/// <param name="Running">在线且运行中设备数。</param>
/// <param name="Offline">离线设备数。</param>
/// <param name="Disabled">已停用设备数。</param>
/// <param name="Connected">当前实时连接数（进程内登记表）。</param>
public sealed record DeviceStatisticsSnapshot(
    int Total,
    int Online,
    int Idle,
    int Running,
    int Offline,
    int Disabled,
    int Connected);
