using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>平台级节点查询与轻量维护（改名/启停）。</summary>
public interface INodeAdmin
{
    /// <summary>分页查询；可按在线状态、启用状态与关键字过滤。</summary>
    Task<PageResult<NodeSummaryDto>> ListAsync(
        PageQuery query,
        string? onlineStatus,
        bool? isEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>节点详情（内嵌该节点全部安装实例）。</summary>
    Task<NodeDetailDto> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>修改节点自定义名/启用状态。</summary>
    Task<NodeDetailDto> UpdateAsync(
        Guid nodeId,
        UpdateNodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>分页查看节点下的安装实例。</summary>
    Task<PageResult<InstallationSummaryDto>> ListInstallationsAsync(
        Guid nodeId,
        PageQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>平台级节点服务实现。</summary>
public sealed class NodeAdmin : INodeAdmin
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public NodeAdmin(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PageResult<NodeSummaryDto>> ListAsync(
        PageQuery query,
        string? onlineStatus,
        bool? isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<DeviceNode>().AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(onlineStatus))
        {
            var status = onlineStatus.Trim();
            if (!DeviceOnlineStatuses.IsKnown(status))
            {
                throw BusinessException.Invalid(
                    $"未知在线状态 '{onlineStatus}'，允许值：{DeviceOnlineStatuses.Offline}/{DeviceOnlineStatuses.Online}。");
            }

            source = string.Equals(status, DeviceOnlineStatuses.Online, StringComparison.Ordinal)
                ? source.Where(static x => x.Installations.Any(
                    i => i.OnlineStatus == DeviceOnlineStatuses.Online))
                : source.Where(static x => !x.Installations.Any(
                    i => i.OnlineStatus == DeviceOnlineStatuses.Online));
        }

        if (isEnabled is { } enabled)
        {
            source = source.Where(x => x.IsEnabled == enabled);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x =>
                x.MachineCode.Contains(keyword)
                || x.DisplayName.Contains(keyword)
                || (x.AutoName != null && x.AutoName.Contains(keyword))
                || (x.IpAddress != null && x.IpAddress.Contains(keyword)));
        }

        long total = await source.LongCountAsync(cancellationToken);
        var nodes = await source
            .OrderByDescending(static x => x.LastSeenAt)
            .ThenBy(static x => x.DisplayName)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .ToListAsync(cancellationToken);

        var installations = await LoadInstallationsAsync(
            [.. nodes.Select(static x => x.Id)], cancellationToken);

        return PageResult<NodeSummaryDto>.Create(
            [.. nodes.Select(node => InventoryMapper.ToSummary(
                node,
                installations.TryGetValue(node.Id, out var rows) ? rows : []))],
            total,
            query);
    }

    /// <inheritdoc />
    public async Task<NodeDetailDto> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await FindAsync(nodeId, cancellationToken);
        return InventoryMapper.ToDetail(node, [.. node.Installations]);
    }

    /// <inheritdoc />
    public async Task<NodeDetailDto> UpdateAsync(
        Guid nodeId,
        UpdateNodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var node = await FindAsync(nodeId, cancellationToken);

        if (request.CustomName is not null)
        {
            var customName = DeviceValidation.Optional(
                request.CustomName, DeviceConventions.MaxNameLength, "自定义设备名");
            node.CustomName = customName;
            // 管理端改过（含清空）后客户端注册不再回填自定义名。
            node.CustomNameSource = DeviceCustomNameSources.Admin;
            node.DisplayName = DeviceNaming.ResolveDisplayName(
                node.AutoName, node.CustomName, node.MachineCode);
        }

        if (request.IsEnabled is { } enabled)
        {
            node.IsEnabled = enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return InventoryMapper.ToDetail(node, [.. node.Installations]);
    }

    /// <inheritdoc />
    public async Task<PageResult<InstallationSummaryDto>> ListInstallationsAsync(
        Guid nodeId,
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var node = await _db.Set<DeviceNode>()
                       .AsNoTracking()
                       .FirstOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
                   ?? throw BusinessException.NotFound(
                       ApiErrorCode.DevicesNodeNotFound,
                       $"物理节点 {nodeId} 不存在。");

        var source = _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(x => x.NodeId == nodeId);

        long total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderBy(static x => x.AppKey)
            .ThenByDescending(static x => x.LastHeartbeatAt)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .ToListAsync(cancellationToken);

        return PageResult<InstallationSummaryDto>.Create(
            [.. rows.Select(x => InventoryMapper.ToSummary(x, node))],
            total,
            query);
    }

    private async Task<DeviceNode> FindAsync(Guid nodeId, CancellationToken cancellationToken) =>
        await _db.Set<DeviceNode>()
            .Include(static x => x.Installations)
            .FirstOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
        ?? throw BusinessException.NotFound(
            ApiErrorCode.DevicesNodeNotFound,
            $"物理节点 {nodeId} 不存在。");

    private async Task<Dictionary<Guid, IReadOnlyList<DeviceInstallation>>> LoadInstallationsAsync(
        IReadOnlyList<Guid> nodeIds,
        CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var rows = await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(x => nodeIds.Contains(x.NodeId))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(static x => x.NodeId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DeviceInstallation>)[.. group]);
    }
}
