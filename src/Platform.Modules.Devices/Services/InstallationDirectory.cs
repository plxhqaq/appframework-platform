using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>安装实例目录实现：按应用校验归属，供备份等模块使用。</summary>
public sealed class InstallationDirectory : IInstallationDirectory
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public InstallationDirectory(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<InstallationDirectoryEntry?> FindAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.Id == installationId)
            .Select(static x => new InstallationDirectoryEntry(
                x.Id,
                x.NodeId,
                x.AppKey,
                x.Node.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstallationDirectoryEntry?> FindByNodeAsync(
        string appKey,
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.NodeId == nodeId)
            .OrderByDescending(static x => x.LastHeartbeatAt)
            .Select(static x => new InstallationDirectoryEntry(
                x.Id,
                x.NodeId,
                x.AppKey,
                x.Node.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
