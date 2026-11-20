using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
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

    /// <summary>删除物理节点；仍有安装实例时拒绝。授权与会话按机器码记账，不随节点行删除而失效。</summary>
    Task DeleteAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);
}

/// <summary>平台级节点服务实现。</summary>
public sealed class NodeAdmin : INodeAdmin
{
    private static readonly Action<ILogger, Guid, Exception?> DisabledPushFailedLog = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1, "NodeDisabledPushFailed"),
        "节点 {NodeId} 停用事件推送失败（踢线标记仍生效）。");

    private readonly PlatformDbContext _db;
    private readonly InstallationConnectionRegistry _connections;
    private readonly IInstallationEventRouter _events;
    private readonly ILogger<NodeAdmin> _logger;

    /// <summary>构造。</summary>
    public NodeAdmin(
        PlatformDbContext db,
        InstallationConnectionRegistry connections,
        IInstallationEventRouter events,
        ILogger<NodeAdmin> logger)
    {
        _db = db;
        _connections = connections;
        _events = events;
        _logger = logger;
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
                request.CustomName, DeviceConventions.MaxNameLength, "节点自定义名");
            // 节点自定义名只有管理端会写；客户端注册只刷新机器名（硬件侧天然标识）。
            node.CustomName = customName;
            node.DisplayName = DeviceNaming.ResolveDisplayName(
                node.AutoName, node.CustomName, node.MachineCode);
        }

        var disabledNow = false;
        if (request.IsEnabled is { } enabled)
        {
            // 只认真实翻转：true→false 才触发踢线，重复停用不重复推事件。
            disabledNow = node.IsEnabled && !enabled;
            node.IsEnabled = enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // 停用：节点组广播 NodeDisabled（同机全部应用受影响），再快照该节点全部连接待踢；
        // 客户端下次上行即断线，重连由注册路径 806 拒绝。
        if (disabledNow)
        {
            try
            {
                await _events.ToNodeAsync(
                    nodeId,
                    DeviceHubEvents.NodeDisabled,
                    new NodeDisabledNotification(nodeId, "disabled_by_admin", DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                DisabledPushFailedLog(_logger, nodeId, ex);
            }

            _connections.MarkNodeDisabled(nodeId);
        }

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

    /// <inheritdoc />
    public async Task DeleteAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await _db.Set<DeviceNode>()
                       .FirstOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
                   ?? throw BusinessException.NotFound(
                       ApiErrorCode.DevicesNodeNotFound,
                       $"物理节点 {nodeId} 不存在。");

        // 安装实例是节点的行子级（FK Restrict）；停用实例同样计数，必须先逐应用删除再删节点。
        var installationCount = await _db.Set<DeviceInstallation>()
            .CountAsync(x => x.NodeId == nodeId, cancellationToken);
        if (installationCount > 0)
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                $"节点 {node.DisplayName} 仍挂有 {installationCount} 个安装实例，请先在对应应用下删除安装实例。");
        }

        _db.Set<DeviceNode>().Remove(node);
        await _db.SaveChangesAsync(cancellationToken);
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
