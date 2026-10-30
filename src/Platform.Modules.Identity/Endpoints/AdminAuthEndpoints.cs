using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Modules.Identity.Services;

namespace Platform.Modules.Identity.Endpoints;

/// <summary>
/// 管理端认证端点：access token 在响应体（仅内存保存），refresh token 只放
/// httpOnly + SameSite=Strict Cookie（D11：与桌面端 body refresh 明确分离）。
/// </summary>
internal static class AdminAuthEndpoints
{
    public static void MapAdminAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api
            .MapGroup($"{PlatformRoutes.AdminPrefix}/auth")
            .WithTags(PlatformModuleKeys.Identity);

        group.MapPost(
                "/login",
                async (
                    HttpContext context,
                    LoginRequest request,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var issued = await auth.AdminLoginAsync(
                            request,
                            context.Connection.RemoteIpAddress?.ToString(),
                            cancellationToken);
                        WriteRefreshCookie(context, issued.RefreshToken, issued.RefreshExpiresAt);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.login",
                            actorId: issued.User.UserId.ToString(),
                            actorName: issued.User.UserName,
                            targetId: issued.User.UserId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new AdminSessionResponse(
                            issued.AccessToken,
                            issued.ExpiresAt,
                            issued.User));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.login",
                            actorId: null,
                            actorName: request.UserName,
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.admin.login")
            .WithSummary("管理端登录（刷新令牌写入 httpOnly Cookie）")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.IdentityLogin);

        group.MapPost(
                "/refresh",
                async (
                    HttpContext context,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var refreshToken = context.Request.Cookies[IdentityConventions.RefreshCookieName];
                    try
                    {
                        var issued = await auth.RefreshAsync(
                            refreshToken,
                            context.Connection.RemoteIpAddress?.ToString(),
                            cancellationToken);
                        WriteRefreshCookie(context, issued.RefreshToken, issued.RefreshExpiresAt);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.refresh",
                            actorId: issued.User.UserId.ToString(),
                            actorName: issued.User.UserName,
                            targetId: issued.User.UserId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new AdminSessionResponse(
                            issued.AccessToken,
                            issued.ExpiresAt,
                            issued.User));
                    }
                    catch (BusinessException exception)
                    {
                        DeleteRefreshCookie(context);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.refresh",
                            actorId: null,
                            actorName: null,
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.admin.refresh")
            .WithSummary("管理端刷新令牌（Cookie 轮换）")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.IdentityLogin);

        group.MapPost(
                "/context",
                async (
                    HttpContext context,
                    SwitchAppContextRequest request,
                    ICurrentPrincipal principal,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    if (principal.IsService
                        || principal.UserId is null
                        || !Guid.TryParse(principal.UserId, out var userId))
                    {
                        throw new BusinessException(
                            ApiErrorCode.Forbidden,
                            "只有用户会话可以切换应用上下文。");
                    }

                    var refreshToken = context.Request.Cookies[IdentityConventions.RefreshCookieName];
                    try
                    {
                        var issued = await auth.SwitchAppContextAsync(
                            userId, request, refreshToken, cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.context.switch",
                            actorId: issued.User.UserId.ToString(),
                            actorName: issued.User.UserName,
                            targetId: issued.User.AppKey,
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new AdminSessionResponse(
                            issued.AccessToken,
                            issued.ExpiresAt,
                            issued.User));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.admin.context.switch",
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            targetId: request.AppKey,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.admin.context")
            .WithSummary("切换应用上下文（换发访问令牌，刷新令牌同步记录应用键）");

        group.MapPost(
                "/logout",
                async (
                    HttpContext context,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var refreshToken = context.Request.Cookies[IdentityConventions.RefreshCookieName];
                    await auth.LogoutAsync(refreshToken, cancellationToken);
                    DeleteRefreshCookie(context);
                    await WriteAuditAsync(
                        context, audit,
                        action: "identity.admin.logout",
                        actorId: null,
                        actorName: null,
                        targetId: null,
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Success(context, "已登出。");
                })
            .WithName("identity.admin.logout")
            .WithSummary("管理端登出（清除 Cookie，幂等）")
            .AllowAnonymous();
    }

    private static void WriteRefreshCookie(HttpContext context, string token, DateTimeOffset expires)
    {
        context.Response.Cookies.Append(
            IdentityConventions.RefreshCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = IdentityConventions.RefreshCookiePath,
                Expires = expires,
                MaxAge = expires - DateTimeOffset.UtcNow,
                IsEssential = true,
            });
    }

    private static void DeleteRefreshCookie(HttpContext context) =>
        context.Response.Cookies.Delete(
            IdentityConventions.RefreshCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = IdentityConventions.RefreshCookiePath,
                IsEssential = true,
            });

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        string action,
        string? actorId,
        string? actorName,
        string? targetId,
        string result,
        string? message) =>
        IdentityEndpoints.WriteAuditAsync(
            context, audit, action, actorId, actorName, targetId, result, message);
}
