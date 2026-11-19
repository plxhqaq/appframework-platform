using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Hubs;

namespace Platform.Modules.Devices.Services;

/// <summary>
/// 安装实例事件路由实现：定向事件经连接登记表定位 installation 组，广播走 app/node 组。
/// 授权变更路由为 (appKey, machineCode) → 节点 → 该应用活跃安装实例，不向 node 组整组广播，
/// 避免同机其它应用的安装实例收到无关提示。离线实例不补发——上线注册后由客户端重连对账覆盖。
/// </summary>
public sealed class InstallationEventRouter : IInstallationEventRouter
{
    private readonly PlatformDbContext _db;
    private readonly InstallationConnectionRegistry _connections;
    private readonly IHubContext<DeviceHub> _hub;

    /// <summary>构造。</summary>
    public InstallationEventRouter(
        PlatformDbContext db,
        InstallationConnectionRegistry connections,
        IHubContext<DeviceHub> hub)
    {
        _db = db;
        _connections = connections;
        _hub = hub;
    }

    /// <inheritdoc />
    public Task ToInstallationAsync(
        string appKey,
        Guid installationId,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        // 归属校验：appKey 不匹配的连接不得收到其它应用的定向事件。
        if (!_connections.TryGetInstallation(installationId, out var connection)
            || !string.Equals(connection.AppKey, appKey, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return SendToInstallationAsync(installationId, eventName, payload, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ToMachineAsync(
        string appKey,
        string machineCode,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineCode);

        var nodeId = await _db.Set<DeviceNode>()
            .AsNoTracking()
            .Where(x => x.MachineCode == machineCode)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (nodeId is null)
        {
            return;
        }

        foreach (var installationId in _connections.GetMachineInstallationIds(appKey, nodeId.Value))
        {
            await SendToInstallationAsync(installationId, eventName, payload, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task ToInstallGuidAsync(
        string appKey,
        Guid installGuid,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return _connections.TryGetInstallationByGuid(appKey, installGuid, out var installationId)
            ? SendToInstallationAsync(installationId, eventName, payload, cancellationToken)
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ToAppAsync(
        string appKey,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        return _hub.Clients
            .Group(DeviceHubPaths.AppGroup(appKey))
            .SendAsync(eventName, payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task ToNodeAsync(
        Guid nodeId,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        return _hub.Clients
            .Group(DeviceHubPaths.NodeGroup(nodeId))
            .SendAsync(eventName, payload, cancellationToken);
    }

    /// <inheritdoc />
    public int AppConnectionCount(string appKey) => _connections.CountAppConnections(appKey);

    private Task SendToInstallationAsync(
        Guid installationId,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        return _hub.Clients
            .Group(DeviceHubPaths.InstallationGroup(installationId))
            .SendAsync(eventName, payload, cancellationToken);
    }
}