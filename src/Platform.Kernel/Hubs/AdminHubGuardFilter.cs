using Microsoft.AspNetCore.SignalR;
using Platform.Kernel.Security;

namespace Platform.Kernel.Hubs;

/// <summary>
/// 管理台 Hub 连接生命周期守卫（D6/D7）：AdminHub 是纯下行通道，浏览器每 30s 调用 <see cref="AdminHub.Touch"/>
/// 承载校验——令牌 <c>exp</c> 过期或 <c>epoch</c> 落后于库值即服务端强制断线，管理台连接才能被及时踢掉。
/// 检测延迟 ≤ Touch 周期 + 守卫缓存窗口。
/// </summary>
public sealed class AdminHubGuardFilter : IHubFilter
{
    private readonly UserTokenEpochGuard _guard;

    /// <summary>构造。</summary>
    public AdminHubGuardFilter(UserTokenEpochGuard guard)
    {
        _guard = guard;
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

        if (!await _guard.IsCurrentAsync(invocationContext.Context.User, invocationContext.Context.ConnectionAborted))
        {
            invocationContext.Context.Abort();
            return null;
        }

        return await next(invocationContext);
    }
}
