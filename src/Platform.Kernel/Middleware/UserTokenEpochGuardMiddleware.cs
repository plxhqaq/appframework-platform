using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Kernel.Errors;
using Platform.Kernel.Security;

namespace Platform.Kernel.Middleware;

/// <summary>
/// 用户级令牌 epoch 守卫（D7）：访问令牌代号落后于库值即 401，封堵 <c>/context</c> 自续期、
/// QR 审批、单次密钥签发等一切"仅凭残余访问令牌续命"的路径。
/// 服务令牌（M2M）豁免——其吊销轴由应用级守卫与设备 Hub 踢线覆盖；缓存窗口由
/// <c>UserEpochGuard:CacheSeconds</c> 控制（默认 30 秒），与 <see cref="AppContextGuardMiddleware"/> 同模式。
/// </summary>
public sealed class UserTokenEpochGuardMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>构造。</summary>
    public UserTokenEpochGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>管道调用。</summary>
    public async Task InvokeAsync(HttpContext context, UserTokenEpochGuard guard)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(PlatformClaimTypes.PrincipalKind)?.Value == PlatformPrincipalKinds.User
            && !await guard.IsCurrentAsync(context.User, context.RequestAborted))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityRefreshTokenRevoked,
                "当前会话已被吊销，请重新登录。");
        }

        await _next(context);
    }
}

/// <summary>中间件注册扩展。</summary>
public static class UserTokenEpochGuardMiddlewareExtensions
{
    /// <summary>启用用户令牌 epoch 守卫；应在 <c>UseAuthentication</c> 之后、授权之前注册。</summary>
    public static IApplicationBuilder UsePlatformUserEpochGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<UserTokenEpochGuardMiddleware>();
}
