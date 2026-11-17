using Microsoft.EntityFrameworkCore;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>全平台设备统计：数据库聚合 + 进程内实时连接数。</summary>
public sealed class DeviceStatisticsService : IDeviceStatistics
{
    private readonly PlatformDbContext _db;
    private readonly DeviceConnectionRegistry _connections;

    /// <summary>构造。</summary>
    public DeviceStatisticsService(PlatformDbContext db, DeviceConnectionRegistry connections)
    {
        _db = db;
        _connections = connections;
    }

    /// <inheritdoc />
    public async Task<DeviceStatisticsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _db.Set<Device>()
            .AsNoTracking()
            .Where(static x => x.MergedIntoDeviceId == null)
            .GroupBy(static _ => 1)
            .Select(static group => new
            {
                Total = group.Count(),
                Online = group.Count(x => x.OnlineStatus == DeviceOnlineStatuses.Online),
                Idle = group.Count(x =>
                    x.OnlineStatus == DeviceOnlineStatuses.Online
                    && x.StationStatus == DeviceStationStatuses.Idle),
                Running = group.Count(x =>
                    x.OnlineStatus == DeviceOnlineStatuses.Online
                    && x.StationStatus == DeviceStationStatuses.Online),
                Disabled = group.Count(x => !x.IsEnabled),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var total = counts?.Total ?? 0;
        var online = counts?.Online ?? 0;
        return new DeviceStatisticsSnapshot(
            total,
            online,
            counts?.Idle ?? 0,
            counts?.Running ?? 0,
            total - online,
            counts?.Disabled ?? 0,
            _connections.Count);
    }
}
