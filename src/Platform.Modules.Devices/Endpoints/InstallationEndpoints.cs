using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices.Endpoints;

/// <summary>应用级安装实例端点：只返回当前应用，跨应用一律 404。</summary>
internal static class InstallationEndpoints
{
    public static void MapInstallationEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var installations = api
            .MapGroup(PlatformRoutes.AdminPrefix + "/installations")
            .WithTags(PlatformModuleKeys.Devices);

        installations.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IInstallationAdmin admin,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? onlineStatus = null,
                    string? branch = null,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await admin.ListAsync(
                            RequireAppKey(principal),
                            query,
                            onlineStatus,
                            branch,
                            cancellationToken));
                })
            .WithName("installations.list")
            .WithSummary("当前应用的安装实例列表")
            .RequirePermission(PermissionCatalog.InstallationRead);

        installations.MapGet(
                "/{installationId:guid}",
                async (
                    HttpContext context,
                    Guid installationId,
                    ICurrentPrincipal principal,
                    IInstallationAdmin admin,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await admin.GetAsync(
                            RequireAppKey(principal), installationId, cancellationToken)))
            .WithName("installations.get")
            .WithSummary("安装实例详情；跨应用返回 404")
            .RequirePermission(PermissionCatalog.InstallationRead);

        installations.MapPatch(
                "/{installationId:guid}",
                async (
                    HttpContext context,
                    Guid installationId,
                    UpdateInstallationRequest request,
                    ICurrentPrincipal principal,
                    IInstallationAdmin admin,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await admin.UpdateAsync(
                            RequireAppKey(principal), installationId, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.update",
                            targetId: installationId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.update",
                            targetId: installationId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("installations.update")
            .WithSummary("应用级启用/停用安装实例")
            .RequirePermission(PermissionCatalog.InstallationManage);

        installations.MapDelete(
                "/{installationId:guid}",
                async (
                    HttpContext context,
                    Guid installationId,
                    ICurrentPrincipal principal,
                    IInstallationAdmin admin,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await admin.DeleteAsync(
                            RequireAppKey(principal), installationId, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.delete",
                            targetId: installationId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, true);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.delete",
                            targetId: installationId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("installations.delete")
            .WithSummary("删除安装实例；在线或有活跃连接时拒绝")
            .RequirePermission(PermissionCatalog.InstallationManage);

        installations.MapPost(
                "/{installationId:guid}/restart",
                async (
                    HttpContext context,
                    Guid installationId,
                    InstallationRestartRequest? request,
                    ICurrentPrincipal principal,
                    IInstallationCommandGateway gateway,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var target = await gateway.GetTargetAsync(appKey, installationId, cancellationToken)
                                     ?? throw BusinessException.NotFound(
                                         ApiErrorCode.DevicesInstallationNotFound,
                                         $"安装实例 {installationId} 不存在。");
                        if (!target.NodeEnabled)
                        {
                            throw new BusinessException(
                                ApiErrorCode.DevicesNodeDisabled,
                                "物理节点已被停用，无法重启安装实例。");
                        }

                        if (!target.IsEnabled)
                        {
                            throw new BusinessException(
                                ApiErrorCode.DevicesInstallationDisabled,
                                "安装实例已被停用，无法重启。");
                        }

                        var reason = DeviceValidation.Optional(
                            request?.Reason, DeviceConventions.MaxRestartReasonLength, "重启原因");
                        await gateway.SendRestartAsync(appKey, installationId, reason, cancellationToken);

                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.restart",
                            targetId: installationId.ToString(),
                            result: AuditResults.Success,
                            message: reason);
                        return ApiResults.Ok(context, new InstallationRestartAccepted(installationId));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "installations.restart",
                            targetId: installationId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("installations.restart")
            .WithSummary("重启在线安装实例")
            .RequirePermission(PermissionCatalog.InstallationRestart);
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问安装实例资源。")
            : principal.AppKey;

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string targetId,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            AppKey = principal.AppKey,
            ActorId = principal.UserId,
            ActorName = principal.UserName,
            Action = action,
            TargetType = "device_installation",
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
