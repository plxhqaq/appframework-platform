namespace Platform.Kernel.Abstractions;

/// <summary>
/// 节点/安装实例统计：平台仪表盘等跨模块视图只读聚合数，不引用 Devices 实体。
/// 由 Devices 模块实现；模块缺席时调用方按全零处理。
/// </summary>
public interface IInventoryStatistics
{
    /// <summary>读取全平台节点与安装实例统计（不区分应用）。</summary>
    Task<InventoryStatisticsSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>节点/安装实例统计快照。</summary>
/// <param name="NodeTotal">物理节点总数。</param>
/// <param name="NodeOnline">任一安装实例在线的节点数。</param>
/// <param name="NodeRunning">任一安装实例在线且工位运行的节点数。</param>
/// <param name="NodeIdle">在线且没有任何安装实例运行的节点数。</param>
/// <param name="NodeOffline">没有任何安装实例在线的节点数。</param>
/// <param name="NodeDisabled">节点开关为停用的节点数。</param>
/// <param name="InstallationTotal">安装实例总数。</param>
/// <param name="InstallationOnline">数据库在线安装实例数。</param>
/// <param name="ConnectedInstallations">当前实时连接数（进程内登记表）。</param>
public sealed record InventoryStatisticsSnapshot(
    int NodeTotal,
    int NodeOnline,
    int NodeRunning,
    int NodeIdle,
    int NodeOffline,
    int NodeDisabled,
    int InstallationTotal,
    int InstallationOnline,
    int ConnectedInstallations);
