using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices.Hubs;

/// <summary>
/// 通用设备 Hub：安装实例注册、心跳、工位状态与下发回执。
/// </summary>
/// <remarks>
/// 安装实例 id 只从连接登记表取，不接受客户端在心跳/工位调用里自报——否则持有一个应用令牌的
/// 安装实例可以改写同应用下其它安装实例的状态。应用隔离轴同样只认令牌声明。
/// </remarks>
[Authorize]
public sealed class DeviceHub : Hub
{
    private static readonly Action<ILogger, Guid, string, Exception?> RegisteredLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, "DeviceHubRegistered"),
            "安装实例 {InstallationId} 在 Hub 注册（连接 {ConnectionId}）。");

    private static readonly Action<ILogger, Guid, string, Exception?> ReplacedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(2, "InstallationSessionReplaced"),
            "安装实例 {InstallationId} 顶替旧连接 {ConnectionId}。");

    private static readonly Action<ILogger, Guid, Exception?> DisconnectedOfflineLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(3, "InstallationMarkedOfflineOnDisconnect"),
            "安装实例 {InstallationId} 最后一条连接断开，已立即标记离线。");

    private static readonly Action<ILogger, Guid, Exception?> DisconnectedOfflineFailedLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(4, "InstallationOfflineOnDisconnectFailed"),
            "安装实例 {InstallationId} 断开后标记离线失败，等待离线扫描兜底。");

    private readonly IInstallationRegistry _registry;
    private readonly IInstallationPresence _presence;
    private readonly InstallationConnectionRegistry _connections;
    private readonly IInstallationCommandAcks _acks;
    private readonly IClock _clock;
    private readonly ILogger<DeviceHub> _logger;

    /// <summary>构造。</summary>
    public DeviceHub(
        IInstallationRegistry registry,
        IInstallationPresence presence,
        InstallationConnectionRegistry connections,
        IInstallationCommandAcks acks,
        IClock clock,
        ILogger<DeviceHub> logger)
    {
        _registry = registry;
        _presence = presence;
        _connections = connections;
        _acks = acks;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // 时间锚点取在注销之前：此后登记的新连接一定比它新，离线落库不得覆盖。
        var disconnectedAt = _clock.UtcNow;
        bool registered = _connections.TryGet(Context.ConnectionId, out var connection);
        _connections.Unregister(Context.ConnectionId);

        // 顶号断开不影响新连接；只有该安装实例的最后一条连接断开才立即离线。
        if (registered && !_connections.HasConnection(connection.InstallationId))
        {
            try
            {
                await _presence.MarkDisconnectedAsync(
                    connection.AppKey, connection.InstallationId, disconnectedAt, CancellationToken.None);
                DisconnectedOfflineLog(_logger, connection.InstallationId, null);
            }
            catch (Exception ex)
            {
                DisconnectedOfflineFailedLog(_logger, connection.InstallationId, ex);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>注册或重连安装实例；同一安装的新连接会顶替旧连接，同机其它应用可并存。</summary>
    /// <param name="request">设备身份与展示信息。</param>
    public async Task<DeviceRegistrationResponse> RegisterInstallation(DeviceRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appKey = RequireAppKey();

        var response = await _registry.RegisterAsync(appKey, request, Context.ConnectionAborted);
        var registration = _connections.Register(
            appKey,
            response.InstallationId,
            response.NodeId,
            request.Identity.InstallGuid,
            Context.ConnectionId);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            DeviceHubPaths.InstallationGroup(response.InstallationId),
            Context.ConnectionAborted);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            DeviceHubPaths.NodeGroup(response.NodeId),
            Context.ConnectionAborted);

        if (registration.Previous is { } previous)
        {
            await Groups.RemoveFromGroupAsync(
                previous.ConnectionId,
                DeviceHubPaths.InstallationGroup(previous.Previous.InstallationId),
                Context.ConnectionAborted);
            await Groups.RemoveFromGroupAsync(
                previous.ConnectionId,
                DeviceHubPaths.NodeGroup(previous.Previous.NodeId),
                Context.ConnectionAborted);
        }

        foreach (var displaced in registration.Displaced)
        {
            await Groups.RemoveFromGroupAsync(
                displaced.ConnectionId,
                DeviceHubPaths.InstallationGroup(displaced.Previous.InstallationId),
                Context.ConnectionAborted);
            await Groups.RemoveFromGroupAsync(
                displaced.ConnectionId,
                DeviceHubPaths.NodeGroup(displaced.Previous.NodeId),
                Context.ConnectionAborted);
            await Clients.Client(displaced.ConnectionId).SendAsync(
                DeviceHubEvents.SessionReplaced,
                new InstallationSessionReplaced("replaced"),
                Context.ConnectionAborted);
            ReplacedLog(_logger, displaced.Previous.InstallationId, displaced.ConnectionId, null);
        }

        RegisteredLog(_logger, response.InstallationId, Context.ConnectionId, null);
        return response;
    }

    /// <summary>心跳：在线/工位/报警一次上报。</summary>
    /// <param name="request">心跳内容。</param>
    public async Task<InstallationSummaryDto> Heartbeat(DeviceHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = RequireConnection();
        return await _presence.HeartbeatAsync(
            connection.AppKey, connection.InstallationId, request, Context.ConnectionAborted);
    }

    /// <summary>工位状态变化（供下发门禁判断是否允许下发）。</summary>
    /// <param name="request">工位状态。</param>
    public async Task<InstallationSummaryDto> ReportStationState(DeviceStationStateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = RequireConnection();
        return await _presence.ReportStationStateAsync(
            connection.AppKey, connection.InstallationId, request, Context.ConnectionAborted);
    }

    /// <summary>配置下发回执：完成命令网关的等待；无等待者时忽略（超时后才到）。</summary>
    /// <param name="ack">回执内容。</param>
    public Task AcknowledgeConfigPush(DeviceConfigPushAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var connection = RequireConnection();
        _acks.Complete(connection.AppKey, connection.InstallationId, ack);
        return Task.CompletedTask;
    }

    private string RequireAppKey()
    {
        var appKey = Context.User?.FindFirst(PlatformClaimTypes.AppCurrent)?.Value;
        return string.IsNullOrWhiteSpace(appKey)
            ? throw new HubException("当前令牌没有应用上下文，无法连接设备 Hub。")
            : appKey;
    }

    private InstallationConnection RequireConnection() =>
        _connections.TryGet(Context.ConnectionId, out var connection)
            ? connection
            : throw new HubException("连接尚未注册安装实例，请先调用 RegisterInstallation。");
}
