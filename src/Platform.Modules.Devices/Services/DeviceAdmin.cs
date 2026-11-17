using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>管理端设备查询与轻量维护（改名/启停）。</summary>
public interface IDeviceAdmin
{
    /// <summary>分页查询；可按在线状态、分支与关键字过滤。</summary>
    Task<PageResult<DeviceSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? onlineStatus,
        string? branch,
        CancellationToken cancellationToken = default);

    /// <summary>设备详情；不存在抛 <see cref="ApiErrorCode.DevicesNotFound"/>。</summary>
    Task<DeviceDetailDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>修改自定义名/启用状态。</summary>
    Task<DeviceDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDeviceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>管理端设备服务实现。</summary>
public sealed class DeviceAdmin : IDeviceAdmin
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public DeviceAdmin(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PageResult<DeviceSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? onlineStatus,
        string? branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<Device>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.MergedIntoDeviceId == null);

        if (!string.IsNullOrWhiteSpace(onlineStatus))
        {
            var status = onlineStatus.Trim();
            if (!DeviceOnlineStatuses.IsKnown(status))
            {
                throw BusinessException.Invalid(
                    $"未知在线状态 '{onlineStatus}'，允许值：{DeviceOnlineStatuses.Offline}/{DeviceOnlineStatuses.Online}。");
            }

            source = source.Where(x => x.OnlineStatus == status);
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var value = branch.Trim();
            source = source.Where(x => x.Branch == value);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x =>
                x.DisplayName.Contains(keyword)
                || x.MachineCode.Contains(keyword)
                || (x.AutoName != null && x.AutoName.Contains(keyword))
                || (x.IpAddress != null && x.IpAddress.Contains(keyword)));
        }

        long total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.LastHeartbeatAt)
            .ThenBy(static x => x.DisplayName)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .ToListAsync(cancellationToken);

        return PageResult<DeviceSummaryDto>.Create(
            [.. rows.Select(DeviceMapper.ToSummary)],
            total,
            query);
    }

    /// <inheritdoc />
    public async Task<DeviceDetailDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var device = await _db.Set<Device>()
                         .AsNoTracking()
                         .FirstOrDefaultAsync(
                             x => x.AppKey == appKey && x.Id == id && x.MergedIntoDeviceId == null,
                             cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.DevicesNotFound,
                         $"设备 {id} 不存在。");
        return DeviceMapper.ToDetail(device);
    }

    /// <inheritdoc />
    public async Task<DeviceDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _db.Set<Device>()
                         .FirstOrDefaultAsync(
                             x => x.AppKey == appKey && x.Id == id && x.MergedIntoDeviceId == null,
                             cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.DevicesNotFound,
                         $"设备 {id} 不存在。");

        if (request.CustomName is not null)
        {
            var customName = DeviceValidation.Optional(
                request.CustomName, DeviceConventions.MaxNameLength, "自定义设备名");
            device.CustomName = customName;
            device.DisplayName = DeviceNaming.Compose(device.AutoName, device.CustomName);
            if (device.DisplayName.Length == 0)
            {
                device.DisplayName = device.MachineCode;
            }
        }

        if (request.IsEnabled is { } enabled)
        {
            device.IsEnabled = enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return DeviceMapper.ToDetail(device);
    }
}
