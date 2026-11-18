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

/// <summary>安装实例命令网关实现：定位实例、按实例/节点分组下发、等待 Ack、发送重启。</summary>
/// <remarks>
/// 在线以连接登记表为实时依据，同时要求数据库在线状态为 <c>online</c>：
/// 连接已断开但离线扫描尚未落库时，命令不会被发到一个已消失的连接上。
/// </remarks>
public sealed class InstallationCommandGateway : IInstallationCommandGateway
{
    private readonly PlatformDbContext _db;
    private readonly InstallationConnectionRegistry _connections;
    private readonly IInstallationCommandAcks _acks;
    private readonly IHubContext<DeviceHub> _hub;

    /// <summary>构造。</summary>
    public InstallationCommandGateway(
        PlatformDbContext db,
        InstallationConnectionRegistry connections,
        IInstallationCommandAcks acks,
        IHubContext<DeviceHub> hub)
    {
        _db = db;
        _connections = connections;
        _acks = acks;
        _hub = hub;
    }

    /// <inheritdoc />
    public async Task<InstallationCommandTarget?> GetTargetAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var installation = await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Include(static x => x.Node)
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Id == installationId,
                cancellationToken);
        if (installation is null)
        {
            return null;
        }

        var online = string.Equals(
                installation.OnlineStatus, DeviceOnlineStatuses.Online, StringComparison.Ordinal)
            && _connections.HasConnection(installation.Id);

        return new InstallationCommandTarget(
            installation.Id,
            installation.NodeId,
            online ? DeviceOnlineStatuses.Online : DeviceOnlineStatuses.Offline,
            installation.StationStatus,
            installation.Branch,
            installation.FrameworkVersion,
            installation.Node.MachineCode,
            installation.IsEnabled,
            installation.Node.IsEnabled);
    }

    /// <inheritdoc />
    public async Task<InstallationPushResult> PushConfigAsync(
        string appKey,
        Guid installationId,
        DeviceConfigPush command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(command);

        if (!TryResolveConnection(appKey, installationId))
        {
            return new InstallationPushResult(false, false, false, null);
        }

        // 先登记等待再发送：回执可能比 SendAsync 返回还快。
        var pending = _acks.Begin(appKey, installationId, command.BackupId);
        try
        {
            await _hub.Clients
                .Group(DeviceHubPaths.InstallationGroup(installationId))
                .SendAsync(DeviceHubEvents.ConfigPush, command, cancellationToken);
        }
        catch
        {
            _acks.Abandon(appKey, installationId, command.BackupId);
            throw;
        }

        try
        {
            var ack = await pending.Task.WaitAsync(timeout, cancellationToken);
            return new InstallationPushResult(true, true, ack.Succeeded, ack.Message);
        }
        catch (TimeoutException)
        {
            _acks.Abandon(appKey, installationId, command.BackupId);
            return new InstallationPushResult(true, false, false, null);
        }
        catch
        {
            _acks.Abandon(appKey, installationId, command.BackupId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SendRestartAsync(
        string appKey,
        Guid installationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        if (!TryResolveConnection(appKey, installationId))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                $"安装实例 {installationId} 当前不在线，无法发送重启命令。");
        }

        await _hub.Clients
            .Group(DeviceHubPaths.InstallationGroup(installationId))
            .SendAsync(DeviceHubEvents.Restart, new DeviceRestart(reason), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> SendNodeRestartAsync(
        Guid nodeId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var node = await _db.Set<DeviceNode>()
                       .AsNoTracking()
                       .FirstOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
                   ?? throw BusinessException.NotFound(
                       ApiErrorCode.DevicesNodeNotFound,
                       $"物理节点 {nodeId} 不存在。");
        if (!node.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesNodeDisabled,
                "物理节点已被停用，无法发送重启命令。");
        }

        int connected = _connections.CountNodeConnections(nodeId);
        if (connected == 0)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                $"物理节点 {nodeId} 当前没有在线安装实例，无法发送重启命令。");
        }

        await _hub.Clients
            .Group(DeviceHubPaths.NodeGroup(nodeId))
            .SendAsync(DeviceHubEvents.Restart, new DeviceRestart(reason), cancellationToken);
        return connected;
    }

    private bool TryResolveConnection(string appKey, Guid installationId) =>
        _connections.TryGetInstallation(installationId, out var connection)
        && string.Equals(connection.AppKey, appKey, StringComparison.Ordinal);
}
