using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Apps;

/// <summary>
/// 应用注册模块：`apps` + `app_api_keys`。它是其余模块的前置——所有业务表的隔离轴 `app_id` 由这里产生。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。业务端点（`/api/v1/admin/apps`、API Key 签发与轮换）
/// 在 S1 落地；届时端点一律经 <c>RequirePermission</c> 并强制 <c>app_id</c> 过滤。
/// </remarks>
public sealed class AppsModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Apps;

    /// <inheritdoc />
    public string DisplayNameKey => "module.apps";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.AppsRead,
        PermissionCatalog.AppsManage,
        PermissionCatalog.AppsApiKeyManage,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        // S1：注册 IAppRegistryService / IApiKeyService，以及本模块的 IPlatformModelContributor。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
