using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>设备目录实现：按应用校验设备归属，供备份等模块使用。</summary>
public sealed class DeviceDirectory : IDeviceDirectory
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public DeviceDirectory(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DeviceDirectoryEntry?> FindAsync(
        string appKey,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return await _db.Set<Device>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.Id == deviceId && x.MergedIntoDeviceId == null)
            .Select(static x => new DeviceDirectoryEntry(x.Id, x.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        _db.Set<Device>()
            .AsNoTracking()
            .AnyAsync(x => x.Id == deviceId && x.MergedIntoDeviceId == null, cancellationToken);
}
