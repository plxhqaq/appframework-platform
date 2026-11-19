using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Services;

namespace Platform.Modules.Identity.Endpoints;

/// <summary>身份管理端点：用户、角色、会话。</summary>
internal static class IdentityAdminEndpoints
{
    public static void MapIdentityAdminEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Identity))
            .WithTags(PlatformModuleKeys.Identity);

        MapUserEndpoints(admin);
        MapRoleEndpoints(admin);
        MapSessionEndpoints(admin);
    }

    private static void MapUserEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/users",
                async (
                    HttpContext context,
                    IUserAdminService users,
                    int page = 1,
                    int pageSize = 20,
                    string? keyword = null,
                    bool? enabled = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(context, await users.ListAsync(query, enabled, cancellationToken));
                })
            .WithName("identity.users.list")
            .WithSummary("用户列表")
            .RequirePermission(PermissionCatalog.IdentityUserRead);

        admin.MapGet(
                "/users/{id:guid}",
                async (HttpContext context, Guid id, IUserAdminService users, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await users.GetAsync(id, cancellationToken)))
            .WithName("identity.users.get")
            .WithSummary("用户详情")
            .RequirePermission(PermissionCatalog.IdentityUserRead);

        admin.MapPost(
                "/users",
                async (
                    HttpContext context,
                    CreateUserRequest request,
                    IUserAdminService users,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "identity.user.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await users.CreateAsync(request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "identity.user.create",
                                    targetType: "user",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "identity.user.create",
                                    targetType: "user",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        });
                })
            .WithName("identity.users.create")
            .WithSummary("创建用户（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.IdentityUserManage);

        admin.MapPatch(
                "/users/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateUserRequest request,
                    IUserAdminService users,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await users.UpdateAsync(id, request, ParseActorId(principal), cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.user.update",
                            targetType: "user",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.user.update",
                            targetType: "user",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.users.update")
            .WithSummary("更新用户显示名/启用状态/角色")
            .RequirePermission(PermissionCatalog.IdentityUserManage);

        admin.MapDelete(
                "/users/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    IUserAdminService users,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    await users.DeleteAsync(id, ParseActorId(principal), cancellationToken);
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "identity.user.delete",
                        targetType: "user",
                        targetId: id.ToString(),
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Success(context, "已删除。");
                })
            .WithName("identity.users.delete")
            .WithSummary("删除用户")
            .RequirePermission(PermissionCatalog.IdentityUserManage);

        admin.MapPost(
                "/users/{id:guid}/password/reset",
                async (
                    HttpContext context,
                    Guid id,
                    ResetUserPasswordRequest request,
                    IUserAdminService users,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var revoked = await users.ResetPasswordAsync(id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.user.password.reset",
                            targetType: "user",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new { revokedSessions = revoked });
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.user.password.reset",
                            targetType: "user",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.users.password.reset")
            .WithSummary("重置用户口令并吊销其全部会话")
            .RequirePermission(PermissionCatalog.IdentityUserPasswordReset);
    }

    private static void MapRoleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/roles",
                async (HttpContext context, IRoleAdminService roles, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await roles.ListAsync(cancellationToken)))
            .WithName("identity.roles.list")
            .WithSummary("角色列表")
            .RequirePermission(PermissionCatalog.IdentityRoleRead);

        admin.MapGet(
                "/roles/{id:guid}",
                async (HttpContext context, Guid id, IRoleAdminService roles, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await roles.GetAsync(id, cancellationToken)))
            .WithName("identity.roles.get")
            .WithSummary("角色详情")
            .RequirePermission(PermissionCatalog.IdentityRoleRead);

        admin.MapPost(
                "/roles",
                async (
                    HttpContext context,
                    CreateRoleRequest request,
                    IRoleAdminService roles,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "identity.role.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await roles.CreateAsync(request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "identity.role.create",
                                    targetType: "role",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "identity.role.create",
                                    targetType: "role",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        });
                })
            .WithName("identity.roles.create")
            .WithSummary("创建角色（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.IdentityRoleManage);

        admin.MapPatch(
                "/roles/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateRoleRequest request,
                    IRoleAdminService roles,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await roles.UpdateAsync(id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.role.update",
                            targetType: "role",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "identity.role.update",
                            targetType: "role",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("identity.roles.update")
            .WithSummary("更新角色显示名/作用域/权限")
            .RequirePermission(PermissionCatalog.IdentityRoleManage);

        admin.MapDelete(
                "/roles/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    IRoleAdminService roles,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    await roles.DeleteAsync(id, cancellationToken);
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "identity.role.delete",
                        targetType: "role",
                        targetId: id.ToString(),
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Success(context, "已删除。");
                })
            .WithName("identity.roles.delete")
            .WithSummary("删除角色")
            .RequirePermission(PermissionCatalog.IdentityRoleManage);
    }

    private static void MapSessionEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/sessions",
                async (
                    HttpContext context,
                    IRefreshTokenService sessions,
                    int page = 1,
                    int pageSize = 20,
                    Guid? userId = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(context, await sessions.ListAsync(query, userId, cancellationToken));
                })
            .WithName("identity.sessions.list")
            .WithSummary("会话列表（分页）")
            .RequirePermission(PermissionCatalog.IdentitySessionRead);

        admin.MapGet(
                "/users/{id:guid}/sessions",
                async (
                    HttpContext context,
                    Guid id,
                    IRefreshTokenService sessions,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await sessions.ListForUserAsync(id, cancellationToken)))
            .WithName("identity.sessions.for-user")
            .WithSummary("用户会话列表")
            .RequirePermission(PermissionCatalog.IdentitySessionRead);

        admin.MapPost(
                "/sessions/{id:guid}/revoke",
                async (
                    HttpContext context,
                    Guid id,
                    IRefreshTokenService sessions,
                    ISessionRevocationNotifier push,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    var revoked = await sessions.RevokeSessionAsync(
                        id,
                        RefreshTokenRevokeReasons.RevokedByAdmin,
                        cancellationToken);
                    await push.NotifyAsync(
                        revoked.UserId,
                        RefreshTokenRevokeReasons.RevokedByAdmin,
                        [new RevokedSessionTarget(revoked.AppKey, revoked.MachineCode, revoked.InstallGuid, revoked.Id)],
                        userWide: false,
                        cancellationToken);
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "identity.session.revoke",
                        targetType: "session",
                        targetId: id.ToString(),
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Ok(context, revoked);
                })
            .WithName("identity.sessions.revoke")
            .WithSummary("吊销一个会话家族")
            .RequirePermission(PermissionCatalog.IdentitySessionRevoke);

        admin.MapPost(
                "/users/{id:guid}/sessions/revoke",
                async (
                    HttpContext context,
                    Guid id,
                    IRefreshTokenService sessions,
                    ISessionRevocationNotifier push,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    // 吊销会清掉活跃行，先抓设备归属再推 UserSessionRevoked。
                    var targets = await push.CaptureUserTargetsAsync(id, cancellationToken);
                    var revoked = await sessions.RevokeAllAsync(
                        id,
                        RefreshTokenRevokeReasons.RevokedByAdmin,
                        cancellationToken);
                    if (revoked > 0)
                    {
                        await push.NotifyAsync(
                            id, RefreshTokenRevokeReasons.RevokedByAdmin, targets, userWide: true, cancellationToken);
                    }
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "identity.sessions.revoke-all",
                        targetType: "user",
                        targetId: id.ToString(),
                        result: AuditResults.Success,
                        message: null);
                    return ApiResults.Ok(context, new { revokedSessions = revoked });
                })
            .WithName("identity.sessions.revoke-all")
            .WithSummary("吊销用户全部会话")
            .RequirePermission(PermissionCatalog.IdentitySessionRevoke);
    }

    private static Guid? ParseActorId(ICurrentPrincipal principal) =>
        Guid.TryParse(principal.UserId, out var id) ? id : null;

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string targetType,
        string? targetId,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            ActorId = principal.UserId,
            ActorName = principal.UserName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
