using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Kernel.Http;

namespace Platform.Kernel.Modules;

/// <summary>
/// 模块公共端点约定。每个模块都必须暴露 <c>GET /api/v1/{module}/info</c>：
/// 联调时先打它确认服务在线、拿到本模块权限码与版本，比翻日志快。
/// </summary>
public static class ModuleEndpointConventions
{
    /// <summary>挂载模块自述端点（匿名可访问，不泄露任何业务数据）。</summary>
    public static RouteHandlerBuilder MapModuleInfo(this IEndpointRouteBuilder api, IPlatformModule module)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(module);

        return api
            .MapGet(
                $"{PlatformRoutes.Module(module.Key)}/info",
                (HttpContext context) => ApiResults.Ok(context, PlatformModuleRegistry.Describe(module)))
            .WithName($"{module.Key}.info")
            .WithSummary($"{module.Key} 模块自述")
            .WithTags(module.Key)
            .AllowAnonymous();
    }
}
