using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Security;

namespace Platform.Kernel.Middleware;

/// <summary>
/// 应用上下文守卫：访问令牌里的当前应用被停用后，已认证请求也不再放行。
/// </summary>
/// <remarks>
/// 访问令牌是自包含的，停用应用不会让已签发的令牌立刻失效；这里在请求路径上补一次短缓存校验。
/// 平台管理员（通配权限）豁免——否则停用应用后没有人能通过管理端把它重新启用。
/// 状态缓存窗口由 <c>AppContextGuard:CacheSeconds</c> 控制（默认 30 秒）。
/// </remarks>
public sealed class AppContextGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AppStateCache _cache;

    /// <summary>构造。</summary>
    public AppContextGuardMiddleware(RequestDelegate next, AppStateCache cache)
    {
        _next = next;
        _cache = cache;
    }

    /// <summary>管道调用。</summary>
    public async Task InvokeAsync(HttpContext context, ICurrentPrincipal principal, IAppDirectory apps)
    {
        if (!principal.IsAuthenticated || principal.HasPermission(PermissionCatalog.Wildcard))
        {
            await _next(context);
            return;
        }

        var appKey = principal.AppKey;
        if (!string.IsNullOrEmpty(appKey))
        {
            if (!_cache.TryGet(appKey, out var enabled))
            {
                enabled = await apps.IsEnabledAsync(appKey, context.RequestAborted);
                _cache.Set(appKey, enabled);
            }

            if (!enabled)
            {
                throw new BusinessException(
                    ApiErrorCode.AppsDisabled,
                    $"应用 {appKey} 已停用，当前访问令牌不能再访问平台。");
            }
        }

        await _next(context);
    }
}

/// <summary>中间件注册扩展。</summary>
public static class AppContextGuardMiddlewareExtensions
{
    /// <summary>启用应用上下文守卫；应在 <c>UseAuthentication</c> 之后、授权之前注册。</summary>
    public static IApplicationBuilder UsePlatformAppContextGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<AppContextGuardMiddleware>();
}
