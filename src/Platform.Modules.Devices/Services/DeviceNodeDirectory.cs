using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>节点目录实现：按节点 id 或机器码提供展示信息，供授权/身份模块读取。</summary>
public sealed class DeviceNodeDirectory : IDeviceNodeDirectory
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public DeviceNodeDirectory(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DeviceNodeDirectoryEntry?> FindAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default) =>
        await _db.Set<DeviceNode>()
            .AsNoTracking()
            .Where(x => x.Id == nodeId)
            .Select(static x => new DeviceNodeDirectoryEntry(x.Id, x.MachineCode, x.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<DeviceNodeDirectoryEntry?> FindByMachineCodeAsync(
        string machineCode,
        CancellationToken cancellationToken = default) =>
        await _db.Set<DeviceNode>()
            .AsNoTracking()
            .Where(x => x.MachineCode == machineCode)
            .Select(static x => new DeviceNodeDirectoryEntry(x.Id, x.MachineCode, x.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DeviceNodeDirectoryEntry>> FindByMachineCodesAsync(
        IReadOnlyCollection<string> machineCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machineCodes);

        var codes = machineCodes
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, DeviceNodeDirectoryEntry>(StringComparer.Ordinal);
        }

        var rows = await _db.Set<DeviceNode>()
            .AsNoTracking()
            .Where(x => codes.Contains(x.MachineCode))
            .Select(static x => new DeviceNodeDirectoryEntry(x.Id, x.MachineCode, x.DisplayName))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(static x => x.MachineCode, StringComparer.Ordinal);
    }
}
