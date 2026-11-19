using Microsoft.AspNetCore.SignalR;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices.Hubs;

/// <summary>
/// 设备 Hub 连接生命周期守卫：每次上行调用前校验令牌 <c>exp</c>，过期即服务端强制断线。
/// SignalR 连接建立后不再走 JWT 中间件，过期令牌否则会无限期存活；业务心跳默认 10s 一次，
/// 检测延迟 ≤ 一个心跳周期。客户端监督循环随后自动换令牌重连（AccessTokenProvider 重连时取新令牌）。
/// 停用踢线：连接若在停用瞬间被快照进"待踢"集合，同样在下次上行时强制断线（重连由注册路径 806/807 拒绝）。
/// </summary>
public sealed class DeviceHubGuardFilter : IHubFilter
{
    private readonly InstallationConnectionRegistry _connections;

    /// <summary>构造。</summary>
    public DeviceHubGuardFilter(InstallationConnectionRegistry connections)
    {
        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var exp = invocationContext.Context.User?.FindFirst("exp")?.Value;
        if (long.TryParse(exp, out var expiresAtSeconds)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAtSeconds)
        {
            invocationContext.Context.Abort();
            return null;
        }

        if (_connections.IsKickPending(invocationContext.Context.ConnectionId))
        {
            invocationContext.Context.Abort();
            return null;
        }

        return await next(invocationContext);
    }
}