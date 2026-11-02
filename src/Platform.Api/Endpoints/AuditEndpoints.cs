using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Audit;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Auditing;
using Platform.Kernel.Http;
using Platform.Kernel.Security;

namespace Platform.Api.Endpoints;

/// <summary>审计查询端点（平台级只读）。</summary>
public static class AuditEndpoints
{
    /// <summary>挂载审计查询。</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet(
                $"{PlatformRoutes.AdminPrefix}/audit-logs",
                async (
                    HttpContext context,
                    IAuditQueryService audit,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? appKey = null,
                    string? action = null,
                    string? actor = null,
                    string? result = null,
                    string? keyword = null,
                    DateTimeOffset? from = null,
                    DateTimeOffset? to = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new AuditLogQuery
                    {
                        Page = page,
                        PageSize = pageSize,
                        AppKey = appKey,
                        Action = action,
                        Actor = actor,
                        Result = result,
                        Keyword = keyword,
                        From = from,
                        To = to,
                    };
                    return ApiResults.Ok(context, await audit.QueryAsync(query, cancellationToken));
                })
            .WithName("platform.audit.list")
            .WithSummary("审计日志查询（按应用/动作/操作者/结果/时间过滤）")
            .WithTags("platform")
            .RequirePermission(PermissionCatalog.AuditRead);

        return api;
    }
}
