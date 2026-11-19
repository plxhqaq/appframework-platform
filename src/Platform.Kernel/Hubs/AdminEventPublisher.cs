using Microsoft.AspNetCore.SignalR;
using Platform.Contracts.Hubs;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Hubs;

/// <summary>管理台事件发布实现：按权限域组播。单副本进程内路由，多副本需 SignalR 背板（接口不变）。</summary>
public sealed class AdminEventPublisher : IAdminEventPublisher
{
    private readonly IHubContext<AdminHub> _hub;

    /// <summary>构造。</summary>
    public AdminEventPublisher(IHubContext<AdminHub> hub)
    {
        _hub = hub;
    }

    /// <inheritdoc />
    public Task PublishAsync(
        string domain,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        return _hub.Clients
            .Group(AdminHubGroups.Domain(domain))
            .SendAsync(eventName, payload, cancellationToken);
    }
}