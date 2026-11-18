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

/// <summary>平台级物理节点端点。节点页跨应用，只对具备 <c>node.*</c> 的平台角色开放。</summary>
internal static class NodeEndpoints
{
    public static void MapNodeEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var nodes = api
            .MapGroup(PlatformRoutes.AdminPrefix + "/nodes")
            .WithTags(PlatformModuleKeys.Devices);

        nodes.MapGet(
                "",
                async (
                    HttpContext context,
                    INodeAdmin admin,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? onlineStatus = null,
                    bool? isEnabled = null,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await admin.ListAsync(query, onlineStatus, isEnabled, cancellationToken));
                })
            .WithName("nodes.list")
            .WithSummary("物理节点列表（跨应用）")
            .RequirePermission(PermissionCatalog.NodeRead);

        nodes.MapGet(
                "/{nodeId:guid}",
                async (
                    HttpContext context,
                    Guid nodeId,
                    INodeAdmin admin,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await admin.GetAsync(nodeId, cancellationToken)))
            .WithName("nodes.get")
            .WithSummary("物理节点详情（内嵌全部安装实例）")
            .RequirePermission(PermissionCatalog.NodeRead);

        nodes.MapPatch(
                "/{nodeId:guid}",
                async (
                    HttpContext context,
                    Guid nodeId,
                    UpdateNodeRequest request,
                    INodeAdmin admin,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await admin.UpdateAsync(nodeId, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "nodes.update",
                            targetId: nodeId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "nodes.update",
                            targetId: nodeId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("nodes.update")
            .WithSummary("修改节点自定义名/启用状态；节点停用即全应用拒绝")
            .RequirePermission(PermissionCatalog.NodeManage);

        nodes.MapPost(
                "/{nodeId:guid}/restart",
                async (
                    HttpContext context,
                    Guid nodeId,
                    NodeRestartRequest? request,
                    IInstallationCommandGateway gateway,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var reason = DeviceValidation.Optional(
                            request?.Reason, DeviceConventions.MaxRestartReasonLength, "重启原因");
                        var count = await gateway.SendNodeRestartAsync(nodeId, reason, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "nodes.restart",
                            targetId: nodeId.ToString(),
                            result: AuditResults.Success,
                            message: reason ?? $"已向 {count} 个在线安装实例发送。");
                        return ApiResults.Ok(context, new NodeRestartAccepted(nodeId, count));
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "nodes.restart",
                            targetId: nodeId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("nodes.restart")
            .WithSummary("重启节点：该节点全部在线安装实例都会收到命令")
            .RequirePermission(PermissionCatalog.NodeRestart);

        nodes.MapGet(
                "/{nodeId:guid}/installations",
                async (
                    HttpContext context,
                    Guid nodeId,
                    INodeAdmin admin,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await admin.ListInstallationsAsync(nodeId, query, cancellationToken));
                })
            .WithName("nodes.installations.list")
            .WithSummary("节点下安装实例分页列表")
            .RequirePermission(PermissionCatalog.NodeRead);
    }

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
            TargetType = "device_node",
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
