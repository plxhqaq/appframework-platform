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

/// <summary>设备管理端端点。设备注册/心跳走 <c>/hubs/devices</c>，这里不重复开口。</summary>
internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Devices))
            .WithTags(PlatformModuleKeys.Devices);

        admin.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDeviceAdmin devices,
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
                        await devices.ListAsync(
                            RequireAppKey(principal), query, onlineStatus, branch, cancellationToken));
                })
            .WithName("devices.list")
            .WithSummary("设备列表")
            .RequirePermission(PermissionCatalog.DeviceRead);

        admin.MapGet(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDeviceAdmin devices,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await devices.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("devices.get")
            .WithSummary("设备详情")
            .RequirePermission(PermissionCatalog.DeviceRead);

        admin.MapPatch(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateDeviceRequest request,
                    ICurrentPrincipal principal,
                    IDeviceAdmin devices,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await devices.UpdateAsync(
                            RequireAppKey(principal), id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "devices.update",
                            targetType: "device",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "devices.update",
                            targetType: "device",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("devices.update")
            .WithSummary("修改设备自定义名/启用状态")
            .RequirePermission(PermissionCatalog.DeviceManage);

        admin.MapPost(
                "/{id:guid}/restart",
                async (
                    HttpContext context,
                    Guid id,
                    DeviceRestartRequest? request,
                    ICurrentPrincipal principal,
                    IDeviceCommandGateway gateway,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var target = await gateway.GetTargetAsync(appKey, id, cancellationToken)
                                     ?? throw BusinessException.NotFound(
                                         ApiErrorCode.DevicesNotFound,
                                         $"设备 {id} 不存在。");
                        if (!target.IsEnabled)
                        {
                            throw new BusinessException(
                                ApiErrorCode.DevicesDisabled,
                                "目标设备已被停用，无法重启。");
                        }

                        var reason = DeviceValidation.Optional(
                            request?.Reason, DeviceConventions.MaxRestartReasonLength, "重启原因");
                        await gateway.SendRestartAsync(appKey, id, reason, cancellationToken);

                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "devices.restart",
                            targetType: "device",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: reason);
                        return ApiResults.Ok(context, new DeviceRestartAccepted(id));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "devices.restart",
                            targetType: "device",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("devices.restart")
            .WithSummary("重启在线设备")
            .RequirePermission(PermissionCatalog.DeviceRestart);
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问设备资源。")
            : principal.AppKey;

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string? targetType,
        string? targetId,
        string result,
        string? message)
    {
        return audit.WriteAsync(new AuditEntry
        {
            AppKey = principal.AppKey,
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
}
