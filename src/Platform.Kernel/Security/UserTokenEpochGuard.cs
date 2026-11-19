using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Security;

/// <summary>
/// 用户级令牌 epoch 守卫（D7）：访问令牌 <c>epoch</c> 声明落后于库值即视为已吊销。
/// 缓存命中不打库；未命中经依赖注入容器现取 <see cref="IUserTokenEpochDirectory"/>（每用户至多 30s 一次查库）。
/// 服务令牌（<c>pkind=service</c>）豁免——M2M 轴由应用级守卫与设备 Hub 踢线覆盖；
/// 非 Guid 主体（开发令牌）无法对账，同样放行。
/// </summary>
public sealed class UserTokenEpochGuard
{
    private readonly UserEpochCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>构造。</summary>
    public UserTokenEpochGuard(UserEpochCache cache, IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    /// <summary>校验主体令牌是否仍是该用户当前有效代号的令牌（true=放行）。</summary>
    public async Task<bool> IsCurrentAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        if (user is null
            || user.Identity?.IsAuthenticated != true
            || user.FindFirst(PlatformClaimTypes.PrincipalKind)?.Value != PlatformPrincipalKinds.User)
        {
            return true;
        }

        if (!Guid.TryParse(user.FindFirst(PlatformClaimTypes.Subject)?.Value, out var userId))
        {
            return true;
        }

        // 缺失声明按 0：bump 之后旧令牌必然落后；从未 bump 过的用户 0 即当前值。
        var tokenEpoch = int.TryParse(
            user.FindFirst(PlatformClaimTypes.TokenEpoch)?.Value,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;

        return await IsUserCurrentAsync(userId, tokenEpoch, cancellationToken);
    }

    /// <summary>按 (userId, tokenEpoch) 校验；供已解析声明的调用方复用。</summary>
    public async Task<bool> IsUserCurrentAsync(Guid userId, int tokenEpoch, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGet(userId, out var cached))
        {
            return tokenEpoch >= cached;
        }

        // 守卫是单例（中间件与 Hub filter 共用），目录实现持有 DbContext（Scoped）：
        // 作用域必须存活到查询完成，否则上下文在 await 前即被释放。
        var current = await ResolveEpochAsync(userId, cancellationToken);
        if (current is null)
        {
            // 用户行不存在或目录未注册：无法对账。吊销轴已由刷新令牌级联删除覆盖，访问令牌残余窗口维持既有口径。
            return true;
        }

        _cache.Set(userId, current.Value);
        return tokenEpoch >= current.Value;
    }

    /// <summary>缓存失效；bump 路径（吊销收口）与本副本守卫共享同一实例，保证即时生效。</summary>
    public void Invalidate(Guid userId) => _cache.Invalidate(userId);

    private async Task<int?> ResolveEpochAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var directory = scope.ServiceProvider.GetService<IUserTokenEpochDirectory>();
        return directory is null ? null : await directory.GetEpochAsync(userId, cancellationToken);
    }
}
