using Microsoft.EntityFrameworkCore;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>全平台节点/安装实例统计：数据库聚合 + 进程内实时连接数。</summary>
public sealed class InventoryStatisticsService : IInventoryStatistics
{
    private readonly PlatformDbContext _db;
    private readonly InstallationConnectionRegistry _connections;

    /// <summary>构造。</summary>
    public InventoryStatisticsService(PlatformDbContext db, InstallationConnectionRegistry connections)
    {
        _db = db;
        _connections = connections;
    }

    /// <inheritdoc />
    public async Task<InventoryStatisticsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await _db.Set<DeviceNode>()
            .AsNoTracking()
            .GroupBy(static _ => 1)
            .Select(static group => new
            {
                Total = group.Count(),
                Disabled = group.Count(static x => !x.IsEnabled),
                Online = group.Count(static x => x.Installations.Any(
                    i => i.OnlineStatus == DeviceOnlineStatuses.Online)),
                Running = group.Count(static x => x.Installations.Any(
                    i => i.OnlineStatus == DeviceOnlineStatuses.Online
                         && i.StationStatus == DeviceStationStatuses.Online)),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var installations = await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .GroupBy(static _ => 1)
            .Select(static group => new
            {
                Total = group.Count(),
                Online = group.Count(static x => x.OnlineStatus == DeviceOnlineStatuses.Online),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var nodeTotal = nodes?.Total ?? 0;
        var nodeOnline = nodes?.Online ?? 0;
        var nodeRunning = nodes?.Running ?? 0;
        return new InventoryStatisticsSnapshot(
            nodeTotal,
            nodeOnline,
            nodeRunning,
            Math.Max(0, nodeOnline - nodeRunning),
            Math.Max(0, nodeTotal - nodeOnline),
            nodes?.Disabled ?? 0,
            installations?.Total ?? 0,
            installations?.Online ?? 0,
            _connections.Count);
    }
}
