using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>设备在线状态维护：心跳、工位、报警与离线扫描。</summary>
public interface IDevicePresence
{
    /// <summary>刷新心跳与报警；返回最新概要（供状态提示推送）。</summary>
    Task<DeviceSummaryDto> HeartbeatAsync(
        string appKey,
        Guid deviceId,
        DeviceHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新工位状态（只接受 <c>idle</c>/<c>online</c>）。</summary>
    Task<DeviceSummaryDto> ReportStationStateAsync(
        string appKey,
        Guid deviceId,
        DeviceStationStateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>把心跳超时的在线设备标记为离线；返回本次标记数量。</summary>
    Task<int> MarkOfflineAsync(CancellationToken cancellationToken = default);
}

/// <summary>设备在线状态实现。</summary>
public sealed class DevicePresence : IDevicePresence
{
    private static readonly Action<ILogger, int, Exception?> OfflineMarkedLog = LoggerMessage.Define<int>(
        LogLevel.Information,
        new EventId(1, "DevicesMarkedOffline"),
        "离线扫描完成：{Count} 台设备标记为离线。");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly DevicesOptions _options;
    private readonly ILogger<DevicePresence> _logger;

    /// <summary>构造。</summary>
    public DevicePresence(
        PlatformDbContext db,
        IClock clock,
        IOptions<DevicesOptions> options,
        ILogger<DevicePresence> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceSummaryDto> HeartbeatAsync(
        string appKey,
        Guid deviceId,
        DeviceHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DeviceHeartbeatStatuses.IsKnown(request.Status))
        {
            throw BusinessException.Invalid(
                $"未知心跳状态 '{request.Status}'，允许值：{DeviceHeartbeatStatuses.Offline}/{DeviceHeartbeatStatuses.Online}/{DeviceHeartbeatStatuses.Idle}。");
        }

        if (!DeviceAlarmLevels.IsKnown(request.AlarmLevel))
        {
            throw BusinessException.Invalid(
                $"未知报警级别 '{request.AlarmLevel}'，允许值：{DeviceAlarmLevels.None}/{DeviceAlarmLevels.Info}/{DeviceAlarmLevels.Warning}/{DeviceAlarmLevels.Error}。");
        }

        var alarmMessage = DeviceValidation.Optional(
            request.AlarmMessage, DeviceConventions.MaxAlarmMessageLength, "报警摘要");

        var device = await FindAsync(appKey, deviceId, cancellationToken);

        switch (request.Status)
        {
            case DeviceHeartbeatStatuses.Offline:
                device.OnlineStatus = DeviceOnlineStatuses.Offline;
                break;
            case DeviceHeartbeatStatuses.Idle:
                device.OnlineStatus = DeviceOnlineStatuses.Online;
                device.StationStatus = DeviceStationStatuses.Idle;
                break;
            case DeviceHeartbeatStatuses.Online:
                device.OnlineStatus = DeviceOnlineStatuses.Online;
                device.StationStatus = DeviceStationStatuses.Online;
                break;
            default:
                break;
        }

        device.AlarmLevel = request.AlarmLevel;
        device.AlarmMessage = string.Equals(request.AlarmLevel, DeviceAlarmLevels.None, StringComparison.Ordinal)
            ? null
            : alarmMessage;
        // 客户端时间可能不准（工控机不联网对时很常见），离线判定必须用服务端时间。
        device.LastHeartbeatAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return DeviceMapper.ToSummary(device);
    }

    /// <inheritdoc />
    public async Task<DeviceSummaryDto> ReportStationStateAsync(
        string appKey,
        Guid deviceId,
        DeviceStationStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DeviceStationStatuses.IsKnown(request.State))
        {
            throw BusinessException.Invalid(
                $"未知工位状态 '{request.State}'，允许值：{DeviceStationStatuses.Idle}/{DeviceStationStatuses.Online}。");
        }

        var device = await FindAsync(appKey, deviceId, cancellationToken);
        device.OnlineStatus = DeviceOnlineStatuses.Online;
        device.StationStatus = request.State;
        device.LastHeartbeatAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return DeviceMapper.ToSummary(device);
    }

    /// <inheritdoc />
    public async Task<int> MarkOfflineAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _db.Set<Device>()
            .Where(x => x.OnlineStatus == DeviceOnlineStatuses.Online)
            .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        int marked = 0;
        foreach (var device in candidates)
        {
            int heartbeat = device.HeartbeatSeconds > 0
                ? device.HeartbeatSeconds
                : _options.DefaultHeartbeatSeconds;
            var threshold = TimeSpan.FromSeconds(
                Math.Max(heartbeat * _options.OfflineMultiplier, _options.MinimumOfflineSeconds));

            if (device.LastHeartbeatAt is null || device.LastHeartbeatAt < now - threshold)
            {
                device.OnlineStatus = DeviceOnlineStatuses.Offline;
                marked++;
            }
        }

        if (marked > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            OfflineMarkedLog(_logger, marked, null);
        }

        return marked;
    }

    private async Task<Device> FindAsync(string appKey, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _db.Set<Device>()
                         .FirstOrDefaultAsync(
                             x => x.AppKey == appKey && x.Id == deviceId,
                             cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.DevicesNotFound,
                         $"设备 {deviceId} 不存在。");
        if (!device.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesDisabled,
                "设备已被管理端停用，停止接收心跳与工位状态。");
        }

        return device;
    }
}
