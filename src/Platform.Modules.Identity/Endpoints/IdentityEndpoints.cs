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

/// <summary>桌面端身份端点：登录、刷新、登出、自助改密。刷新令牌放在响应体。</summary>
internal static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api
            .MapGroup(PlatformRoutes.Module(PlatformModuleKeys.Identity))
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
                        var issued = await auth.LoginAsync(
                            request,
                            context.Connection.RemoteIpAddress?.ToString(),
                            cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.login",
                            actorId: issued.User.UserId.ToString(),
                            actorName: issued.User.UserName,
                            targetId: issued.User.UserId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, issued);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.login",
                            actorId: null,
                            actorName: request.UserName,
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.login")
            .WithSummary("用户名口令登录（桌面端：刷新令牌在响应体）")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.IdentityLogin);

        group.MapPost(
                "/refresh",
                async (
                    HttpContext context,
                    RefreshTokenRequest request,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var issued = await auth.RefreshAsync(
                            request.RefreshToken,
                            context.Connection.RemoteIpAddress?.ToString(),
                            cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.refresh",
                            actorId: issued.User.UserId.ToString(),
                            actorName: issued.User.UserName,
                            targetId: issued.User.UserId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new RefreshTokenResponse(
                            issued.AccessToken,
                            issued.ExpiresAt,
                            issued.RefreshToken,
                            issued.RefreshExpiresAt,
                            issued.User));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.refresh",
                            actorId: null,
                            actorName: null,
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.refresh")
            .WithSummary("刷新并轮换令牌（旧刷新令牌立即失效）")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.IdentityLogin);

        group.MapPost(
                "/logout",
                async (
                    HttpContext context,
                    LogoutRequest? request,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    await auth.LogoutAsync(request?.RefreshToken, cancellationToken);
                    await WriteAuditAsync(
                        context, audit,
                        action: "identity.logout",
                        actorId: null,
                        actorName: null,
                        targetId: null,
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Success(context, "已登出。");
                })
            .WithName("identity.logout")
            .WithSummary("登出（幂等）")
            .AllowAnonymous();

        group.MapPost(
                "/password/change",
                async (
                    HttpContext context,
                    ChangePasswordRequest request,
                    IIdentityAuthService auth,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    var userId = ParseUserId(principal);
                    try
                    {
                        var result = await auth.ChangePasswordAsync(userId, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.password.change",
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            targetId: userId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            action: "identity.password.change",
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            targetId: userId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.password.change")
            .WithSummary("修改自己的口令（成功后吊销全部刷新令牌）")
            .RequireAuthorization();
    }

    internal static Guid ParseUserId(ICurrentPrincipal principal)
    {
        if (Guid.TryParse(principal.UserId, out var userId))
        {
            return userId;
        }

        throw new BusinessException(ApiErrorCode.Unauthorized, "令牌缺少有效的用户标识。");
    }

    internal static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        string action,
        string? actorId,
        string? actorName,
        string? targetId,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            AppKey = null,
            ActorId = actorId,
            ActorName = actorName,
            Action = action,
            TargetType = "identity",
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
