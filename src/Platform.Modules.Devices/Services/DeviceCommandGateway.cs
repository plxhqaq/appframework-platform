using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Hubs;

namespace Platform.Modules.Devices.Services;

/// <summary>设备命令网关实现：定位设备、按设备分组下发、等待 Ack、发送重启。</summary>
/// <remarks>
/// 设备在线以连接登记表为实时依据，同时要求数据库在线状态为 <c>online</c>：
/// 连接已断开但离线扫描尚未落库时，命令不会被发到一个已消失的连接上。
/// </remarks>
public sealed class DeviceCommandGateway : IDeviceCommandGateway
{
    private readonly PlatformDbContext _db;
    private readonly DeviceConnectionRegistry _connections;
    private readonly IDeviceCommandAcks _acks;
    private readonly IHubContext<DeviceHub> _hub;

    /// <summary>构造。</summary>
    public DeviceCommandGateway(
        PlatformDbContext db,
        DeviceConnectionRegistry connections,
        IDeviceCommandAcks acks,
        IHubContext<DeviceHub> hub)
    {
        _db = db;
        _connections = connections;
        _acks = acks;
        _hub = hub;
    }

    /// <inheritdoc />
    public async Task<DeviceCommandTarget?> GetTargetAsync(
        string appKey,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var device = await _db.Set<Device>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Id == deviceId && x.MergedIntoDeviceId == null,
                cancellationToken);
        if (device is null)
        {
            return null;
        }

        var online = string.Equals(
                device.OnlineStatus, DeviceOnlineStatuses.Online, StringComparison.Ordinal)
            && _connections.HasConnection(device.Id);

        return new DeviceCommandTarget(
            device.Id,
            online ? DeviceOnlineStatuses.Online : DeviceOnlineStatuses.Offline,
            device.StationStatus,
            device.Branch,
            device.FrameworkVersion,
            device.MachineCode,
            device.IsEnabled);
    }

    /// <inheritdoc />
    public async Task<DevicePushResult> PushConfigAsync(
        string appKey,
        Guid deviceId,
        DeviceConfigPush command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(command);

        if (!TryResolveConnection(appKey, deviceId))
        {
            return new DevicePushResult(false, false, false, null);
        }

        // 先登记等待再发送：回执可能比 SendAsync 返回还快。
        var pending = _acks.Begin(appKey, deviceId, command.BackupId);
        try
        {
            await _hub.Clients
                .Group(DeviceHubPaths.DeviceGroup(deviceId))
                .SendAsync(DeviceHubEvents.ConfigPush, command, cancellationToken);
        }
        catch
        {
            _acks.Abandon(appKey, deviceId, command.BackupId);
            throw;
        }

        try
        {
            var ack = await pending.Task.WaitAsync(timeout, cancellationToken);
            return new DevicePushResult(true, true, ack.Succeeded, ack.Message);
        }
        catch (TimeoutException)
        {
            _acks.Abandon(appKey, deviceId, command.BackupId);
            return new DevicePushResult(true, false, false, null);
        }
        catch
        {
            _acks.Abandon(appKey, deviceId, command.BackupId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SendRestartAsync(
        string appKey,
        Guid deviceId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        if (!TryResolveConnection(appKey, deviceId))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                $"设备 {deviceId} 当前不在线，无法发送重启命令。");
        }

        await _hub.Clients
            .Group(DeviceHubPaths.DeviceGroup(deviceId))
            .SendAsync(DeviceHubEvents.Restart, new DeviceRestart(reason), cancellationToken);
    }

    private bool TryResolveConnection(string appKey, Guid deviceId) =>
        _connections.TryGetDevice(deviceId, out var connection)
        && string.Equals(connection.AppKey, appKey, StringComparison.Ordinal);
}
