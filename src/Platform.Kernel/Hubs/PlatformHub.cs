using Microsoft.AspNetCore.SignalR;
using Platform.Contracts;
using Platform.Contracts.Hubs;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Hubs;

/// <summary>
/// 平台唯一 SignalR Hub：业务用事件名区分。**所有推送都只是提示**，
/// 客户端收到后必须回拉接口重新校验，不得把推送内容当作事实来源。
/// </summary>
public sealed class PlatformHub : Hub
{
    private readonly IPlatformPushCoordinator _coordinator;

    /// <summary>构造。</summary>
    public PlatformHub(IPlatformPushCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var appKey = Context.User?.FindFirst(PlatformClaimTypes.AppCurrent)?.Value;
        if (!string.IsNullOrWhiteSpace(appKey))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PlatformHubGroups.App(appKey));
            _coordinator.RegisterConnection(Context.ConnectionId, appKey);
        }

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _coordinator.UnregisterConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>配置下发回执：客户端应用（或拒绝）后调用。</summary>
    /// <param name="ack">回执内容。</param>
    public Task ConfigPushAck(ConfigPushAck ack)
    {
        var appKey = Context.User?.FindFirst(PlatformClaimTypes.AppCurrent)?.Value;
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return Task.CompletedTask;
        }

        _coordinator.CompleteAck(appKey, ack.BackupId, ack.Succeeded, ack.Message);
        return Task.CompletedTask;
    }
}
