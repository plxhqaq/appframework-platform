using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>应用级安装实例查询与启停。</summary>
public interface IInstallationAdmin
{
    /// <summary>分页查询当前应用的安装实例。</summary>
    Task<PageResult<InstallationSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? onlineStatus,
        string? branch,
        CancellationToken cancellationToken = default);

    /// <summary>安装实例详情；跨应用返回 404。</summary>
    Task<InstallationDetailDto> GetAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default);

    /// <summary>应用级改名与启用/停用安装实例。</summary>
    Task<InstallationDetailDto> UpdateAsync(
        string appKey,
        Guid installationId,
        UpdateInstallationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>应用级安装实例服务实现。</summary>
public sealed class InstallationAdmin : IInstallationAdmin
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public InstallationAdmin(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PageResult<InstallationSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? onlineStatus,
        string? branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(query);

        var source = from installation in _db.Set<DeviceInstallation>().AsNoTracking()
                     join node in _db.Set<DeviceNode>().AsNoTracking()
                         on installation.NodeId equals node.Id
                     where installation.AppKey == appKey
                     select new { installation, node };

        if (!string.IsNullOrWhiteSpace(onlineStatus))
        {
            var status = onlineStatus.Trim();
            if (!DeviceOnlineStatuses.IsKnown(status))
            {
                throw BusinessException.Invalid(
                    $"未知在线状态 '{onlineStatus}'，允许值：{DeviceOnlineStatuses.Offline}/{DeviceOnlineStatuses.Online}。");
            }

            source = source.Where(x => x.installation.OnlineStatus == status);
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var value = branch.Trim();
            source = source.Where(x => x.installation.Branch == value);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x =>
                x.node.MachineCode.Contains(keyword)
                || x.node.DisplayName.Contains(keyword)
                || (x.node.AutoName != null && x.node.AutoName.Contains(keyword))
                || (x.node.IpAddress != null && x.node.IpAddress.Contains(keyword))
                || (x.installation.DisplayName != null && x.installation.DisplayName.Contains(keyword))
                || (x.installation.ClientName != null && x.installation.ClientName.Contains(keyword)));
        }

        long total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.installation.LastHeartbeatAt)
            .ThenBy(static x => x.node.DisplayName)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .Select(static x => new { x.installation, x.node })
            .ToListAsync(cancellationToken);

        return PageResult<InstallationSummaryDto>.Create(
            [.. rows.Select(x => InventoryMapper.ToSummary(x.installation, x.node))],
            total,
            query);
    }

    /// <inheritdoc />
    public async Task<InstallationDetailDto> GetAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await FindAsync(appKey, installationId, cancellationToken);
        return InventoryMapper.ToDetail(installation, installation.Node);
    }

    /// <inheritdoc />
    public async Task<InstallationDetailDto> UpdateAsync(
        string appKey,
        Guid installationId,
        UpdateInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var installation = await FindAsync(appKey, installationId, cancellationToken);
        if (request.CustomName is not null)
        {
            var customName = DeviceValidation.Optional(
                request.CustomName, DeviceConventions.MaxNameLength, "安装实例自定义名");
            // 安装实例自定义名只有管理端会写；客户端注册只刷新应用内录入名。
            installation.CustomName = customName;
            installation.DisplayName = DeviceNaming.ResolveInstallationDisplayName(
                installation.ClientName, installation.CustomName);
        }

        if (request.IsEnabled is { } enabled)
        {
            installation.IsEnabled = enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return InventoryMapper.ToDetail(installation, installation.Node);
    }

    private async Task<DeviceInstallation> FindAsync(
        string appKey, Guid installationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return await _db.Set<DeviceInstallation>()
                   .Include(static x => x.Node)
                   .FirstOrDefaultAsync(
                       x => x.AppKey == appKey && x.Id == installationId,
                       cancellationToken)
               ?? throw BusinessException.NotFound(
                   ApiErrorCode.DevicesInstallationNotFound,
                   $"安装实例 {installationId} 不存在。");
    }
}
