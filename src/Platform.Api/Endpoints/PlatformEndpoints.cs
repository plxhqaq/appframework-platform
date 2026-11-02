using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Http;
using Platform.Kernel.Modules;

namespace Platform.Api.Endpoints;

/// <summary>平台级端点（不属于任何业务模块）：健康检查、模块清单、权限目录、当前主体。</summary>
public static class PlatformEndpoints
{
    /// <summary>挂载平台级端点。</summary>
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        // 存活探针：只证明进程还在，不查库（避免库抖动导致容器被反复重启）
        api.MapGet("/healthz", (HttpContext context) => ApiResults.Ok(context, new { status = "ok" }))
            .WithTags("platform")
            .AllowAnonymous();

        // 就绪探针：带 ready 标签的检查（含 PostgreSQL 连通性）
        api.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("ready"),
            ResponseWriter = static async (context, report) =>
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(ApiResult.Ok(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(static e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        duration = e.Value.Duration.TotalMilliseconds,
                        error = e.Value.Exception?.Message,
                    }),
                }));
            },
        }).AllowAnonymous();

        var platform = api.MapGroup(PlatformRoutes.Module("platform")).WithTags("platform");

        platform.MapGet(
                "/modules",
                (HttpContext context, PlatformModuleRegistry registry) => ApiResults.Ok(context, registry.Describe()))
            .WithName("platform.modules")
            .WithSummary("模块清单与各自权限码")
            .AllowAnonymous();

        platform.MapGet(
                "/permissions",
                (HttpContext context) => ApiResults.Ok(context, PermissionCatalog.All))
            .WithName("platform.permissions")
            .WithSummary("权限码目录（后台权限树与客户端权限映射的唯一来源）")
            .AllowAnonymous();

        platform.MapGet(
                "/whoami",
                (HttpContext context, ICurrentPrincipal principal) => ApiResults.Ok(context, new WhoAmIResponse(
                    principal.IsAuthenticated,
                    principal.UserId,
                    principal.UserName,
                    principal.IsService,
                    principal.IsTemporary,
                    principal.AppKey,
                    principal.Roles,
                    [.. principal.Permissions],
                    principal.AppKeys)))
            .WithName("platform.whoami")
            .WithSummary("当前令牌解析结果（联调令牌声明是否正确的第一站）")
            .RequireAuthorization();

        return api;
    }
}
