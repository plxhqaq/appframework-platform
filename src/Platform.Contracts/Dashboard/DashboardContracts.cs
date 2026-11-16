namespace Platform.Contracts.Dashboard;

/// <summary>总览仪表盘快照：系统状态、运行环境、资源占用与设备统计。</summary>
/// <param name="ServerTime">服务端当前时间。</param>
/// <param name="StartedAt">平台进程启动时间。</param>
/// <param name="UptimeSeconds">平台进程已运行秒数。</param>
/// <param name="BootTime">服务器操作系统开机时间。</param>
/// <param name="SystemUptimeSeconds">服务器自开机以来已运行秒数。</param>
/// <param name="System">进程与资源状态。</param>
/// <param name="Devices">设备统计。</param>
/// <param name="Platform">运行平台信息。</param>
public sealed record PlatformDashboardDto(
    DateTimeOffset ServerTime,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    DateTimeOffset BootTime,
    long SystemUptimeSeconds,
    SystemStatusDto System,
    DeviceStatisticsDto Devices,
    PlatformInfoDto Platform);

/// <summary>系统状态与资源占用。</summary>
/// <param name="Status">存活状态，正常为 <c>ok</c>。</param>
/// <param name="CpuUsagePercent">进程 CPU 占用率（0–100，按全部逻辑核归一）。</param>
/// <param name="MemoryUsagePercent">进程工作集占可用内存的比例（0–100）。</param>
/// <param name="WorkingSetBytes">进程工作集字节数。</param>
/// <param name="ManagedMemoryBytes">托管堆已用字节数。</param>
/// <param name="TotalMemoryBytes">进程可用的总内存字节数（容器感知）。</param>
/// <param name="AvailableStorageBytes">Blob 根所在卷的可用空间；无法探测时为 <c>null</c>。</param>
/// <param name="ProcessorCount">逻辑处理器数量。</param>
/// <param name="ServerCpuUsagePercent">整机 CPU 占用率（0–100）；采样窗口未就绪或平台不支持时为 <c>null</c>。</param>
/// <param name="ServerMemoryUsagePercent">整机物理内存占用率（0–100）；无法探测时为 <c>null</c>。</param>
/// <param name="ServerTotalMemoryBytes">整机物理内存总量；无法探测时为 <c>null</c>。</param>
/// <param name="ServerAvailableMemoryBytes">整机可用物理内存；无法探测时为 <c>null</c>。</param>
public sealed record SystemStatusDto(
    string Status,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long TotalMemoryBytes,
    long? AvailableStorageBytes,
    int ProcessorCount,
    double? ServerCpuUsagePercent,
    double? ServerMemoryUsagePercent,
    long? ServerTotalMemoryBytes,
    long? ServerAvailableMemoryBytes);

/// <summary>设备统计：在线/离线以数据库状态为准，<paramref name="Connected"/> 为实时连接数。</summary>
public sealed record DeviceStatisticsDto(
    int Total,
    int Online,
    int Idle,
    int Running,
    int Offline,
    int Disabled,
    int Connected);

/// <summary>运行平台信息。</summary>
public sealed record PlatformInfoDto(
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string RuntimeVersion,
    string EnvironmentName,
    string PlatformVersion,
    int ModuleCount);
