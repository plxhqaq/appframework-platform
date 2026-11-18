using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Identity.Services;

namespace Platform.Modules.Identity.Endpoints;

/// <summary>单次密钥兑换与扫码登录端点。兑换/扫码创建/轮询为匿名入口，审批走管理端权限。</summary>
internal static class SingleLoginEndpoints
{
    public static void MapSingleLoginEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var group = api
            .MapGroup(PlatformRoutes.Module(PlatformModuleKeys.Identity))
            .WithTags(PlatformModuleKeys.Identity);

        group.MapPost(
                "/singlekey/redeem",
                async (
                    HttpContext context,
                    RedeemSingleLoginKeyRequest request,
                    ISingleLoginKeyService keys,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var session = await keys.RedeemAsync(request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            actorId: null,
                            actorName: null,
                            action: "identity.singlekey.redeem",
                            targetId: null,
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, session);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            actorId: null,
                            actorName: null,
                            action: "identity.singlekey.redeem",
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.singlekey.redeem")
            .WithSummary("兑换 6 位单次密钥，签发临时访问令牌")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.SingleLoginRedeem);

        group.MapPost(
                "/qr/sessions",
                async (
                    HttpContext context,
                    CreateQrSessionRequest request,
                    IQrLoginService qr,
                    CancellationToken cancellationToken) =>
                {
                    var created = await qr.CreateAsync(
                        request,
                        context.Connection.RemoteIpAddress?.ToString(),
                        $"{context.Request.Scheme}://{context.Request.Host}",
                        cancellationToken);
                    return ApiResults.Ok(context, created);
                })
            .WithName("identity.qr.sessions.create")
            .WithSummary("创建扫码登录会话（返回二维码载荷）")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.QrSession);

        group.MapGet(
                "/qr/sessions/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    IQrLoginService qr,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await qr.GetStatusAsync(id, cancellationToken)))
            .WithName("identity.qr.sessions.status")
            .WithSummary("轮询扫码会话状态；批准后领取临时访问令牌")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.QrSession);

        group.MapPost(
                "/qr/sessions/{id:guid}/cancel",
                async (
                    HttpContext context,
                    Guid id,
                    IQrLoginService qr,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await qr.CancelAsync(id, cancellationToken)))
            .WithName("identity.qr.sessions.cancel")
            .WithSummary("客户端放弃扫码：仅取消待审批会话，幂等，不签发令牌")
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.QrSession);
    }

    public static void MapSingleLoginAdminEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Identity))
            .WithTags(PlatformModuleKeys.Identity);

        admin.MapPost(
                "/singlekeys",
                async (
                    HttpContext context,
                    IssueSingleLoginKeyRequest request,
                    ISingleLoginKeyService keys,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    var actorId = IdentityEndpoints.ParseUserId(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "identity.singlekey.issue",
                        async ct =>
                        {
                            try
                            {
                                var issued = await keys.IssueAsync(actorId, principal.AppKey, request, ct);
                                await WriteAuditAsync(
                                    context, audit,
                                    actorId: principal.UserId,
                                    actorName: principal.UserName,
                                    action: "identity.singlekey.issue",
                                    targetId: null,
                                    result: AuditResults.Success,
                                    message: $"有效期 {issued.DurationMinutes} 分钟，到期 {issued.ExpiresAt:O}。");
                                return ApiResult.Ok(issued);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit,
                                    actorId: principal.UserId,
                                    actorName: principal.UserName,
                                    action: "identity.singlekey.issue",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        });
                })
            .WithName("identity.singlekeys.issue")
            .WithSummary("签发 6 位单次密钥（支持 Idempotency-Key；明文只返回一次）")
            .RequirePermission(PermissionCatalog.IdentitySingleKeyIssue)
            .RequireNonTemporaryPrincipal();

        admin.MapGet(
                "/qr/sessions/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    IQrLoginService qr,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await qr.GetAsync(id, cancellationToken)))
            .WithName("identity.qr.sessions.get")
            .WithSummary("按 id 取单个扫码会话（手机扫码直达审批页）")
            .RequirePermission(PermissionCatalog.IdentityQrApprove)
            .RequireNonTemporaryPrincipal();

        admin.MapGet(
                "/qr/sessions",
                async (
                    HttpContext context,
                    IQrLoginService qr,
                    int page = 1,
                    int pageSize = 20,
                    string? status = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(context, await qr.ListAsync(query, status, cancellationToken));
                })
            .WithName("identity.qr.sessions.list")
            .WithSummary("扫码会话列表（默认全部；status=pending/expired/approved/rejected）")
            .RequirePermission(PermissionCatalog.IdentityQrApprove)
            .RequireNonTemporaryPrincipal();

        admin.MapPost(
                "/qr/sessions/{id:guid}/approve",
                async (
                    HttpContext context,
                    Guid id,
                    ApproveQrSessionRequest request,
                    IQrLoginService qr,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var summary = await qr.ApproveAsync(
                            id,
                            request,
                            IdentityEndpoints.ParseUserId(principal),
                            principal.UserName,
                            cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            action: "identity.qr.approve",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, summary);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            action: "identity.qr.approve",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.qr.sessions.approve")
            .WithSummary("批准扫码登录并签发临时令牌")
            .RequirePermission(PermissionCatalog.IdentityQrApprove)
            .RequireNonTemporaryPrincipal();

        admin.MapPost(
                "/qr/sessions/{id:guid}/reject",
                async (
                    HttpContext context,
                    Guid id,
                    IQrLoginService qr,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var summary = await qr.RejectAsync(
                            id,
                            IdentityEndpoints.ParseUserId(principal),
                            principal.UserName,
                            cancellationToken);
                        await WriteAuditAsync(
                            context, audit,
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            action: "identity.qr.reject",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, summary);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit,
                            actorId: principal.UserId,
                            actorName: principal.UserName,
                            action: "identity.qr.reject",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.qr.sessions.reject")
            .WithSummary("拒绝扫码登录")
            .RequirePermission(PermissionCatalog.IdentityQrApprove)
            .RequireNonTemporaryPrincipal();
    }

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        string? actorId,
        string? actorName,
        string action,
        string? targetId,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            ActorId = actorId,
            ActorName = actorName,
            Action = action,
            TargetType = action.StartsWith("identity.qr.", StringComparison.Ordinal) ? "qr_login_session" : "single_login_key",
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
