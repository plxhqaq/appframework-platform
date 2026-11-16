using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices.Hubs;

/// <summary>
/// 通用设备 Hub：注册、心跳、工位状态与下发回执。
/// </summary>
/// <remarks>
/// 设备 id 只从连接登记表取，不接受客户端在心跳/工位调用里自报——否则持有一个应用令牌的
/// 设备可以改写同应用下其它设备的状态。应用隔离轴同样只认令牌声明。
/// </remarks>
[Authorize]
public sealed class DeviceHub : Hub
{
    private static readonly Action<ILogger, Guid, string, Exception?> RegisteredLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, "DeviceHubRegistered"),
            "设备 {DeviceId} 在 Hub 注册（连接 {ConnectionId}）。");

    private static readonly Action<ILogger, Guid, string, Exception?> ReplacedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(2, "DeviceSessionReplaced"),
            "设备 {DeviceId} 顶替旧连接 {ConnectionId}。");

    private readonly IDeviceRegistry _registry;
    private readonly IDevicePresence _presence;
    private readonly DeviceConnectionRegistry _connections;
    private readonly IDeviceCommandAcks _acks;
    private readonly ILogger<DeviceHub> _logger;

    /// <summary>构造。</summary>
    public DeviceHub(
        IDeviceRegistry registry,
        IDevicePresence presence,
        DeviceConnectionRegistry connections,
        IDeviceCommandAcks acks,
        ILogger<DeviceHub> logger)
    {
        _registry = registry;
        _presence = presence;
        _connections = connections;
        _acks = acks;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Unregister(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>注册或重连设备；同一设备的新连接会顶替旧连接。</summary>
    /// <param name="request">设备身份与展示信息。</param>
    public async Task<DeviceRegistrationResponse> RegisterDevice(DeviceRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appKey = RequireAppKey();

        var response = await _registry.RegisterAsync(appKey, request, Context.ConnectionAborted);
        var replaced = _connections.Register(appKey, response.DeviceId, Context.ConnectionId);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            DeviceHubPaths.DeviceGroup(response.DeviceId),
            Context.ConnectionAborted);

        if (!string.IsNullOrEmpty(replaced))
        {
            await Groups.RemoveFromGroupAsync(
                replaced,
                DeviceHubPaths.DeviceGroup(response.DeviceId),
                Context.ConnectionAborted);
            await Clients.Client(replaced).SendAsync(
                DeviceHubEvents.SessionReplaced,
                new DeviceSessionReplaced("replaced"),
                Context.ConnectionAborted);
            ReplacedLog(_logger, response.DeviceId, replaced, null);
        }

        RegisteredLog(_logger, response.DeviceId, Context.ConnectionId, null);
        return response;
    }

    /// <summary>心跳：在线/工位/报警一次上报。</summary>
    /// <param name="request">心跳内容。</param>
    public async Task<DeviceSummaryDto> Heartbeat(DeviceHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = RequireConnection();
        return await _presence.HeartbeatAsync(
            connection.AppKey, connection.DeviceId, request, Context.ConnectionAborted);
    }

    /// <summary>工位状态变化（供 S3 门禁判断是否允许下发）。</summary>
    /// <param name="request">工位状态。</param>
    public async Task<DeviceSummaryDto> ReportStationState(DeviceStationStateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = RequireConnection();
        return await _presence.ReportStationStateAsync(
            connection.AppKey, connection.DeviceId, request, Context.ConnectionAborted);
    }

    /// <summary>配置下发回执：完成命令网关的等待；无等待者时忽略（超时后才到）。</summary>
    /// <param name="ack">回执内容。</param>
    public Task AcknowledgeConfigPush(DeviceConfigPushAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var connection = RequireConnection();
        _acks.Complete(connection.AppKey, connection.DeviceId, ack);
        return Task.CompletedTask;
    }

    private string RequireAppKey()
    {
        var appKey = Context.User?.FindFirst(PlatformClaimTypes.App)?.Value;
        return string.IsNullOrWhiteSpace(appKey)
            ? throw new HubException("当前令牌没有应用上下文，无法连接设备 Hub。")
            : appKey;
    }

    private DeviceConnection RequireConnection() =>
        _connections.TryGet(Context.ConnectionId, out var connection)
            ? connection
            : throw new HubException("连接尚未注册设备，请先调用 RegisterDevice。");
}
