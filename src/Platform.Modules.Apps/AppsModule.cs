using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Modules;
using Platform.Modules.Apps.Endpoints;
using Platform.Modules.Apps.Persistence;
using Platform.Modules.Apps.Services;

namespace Platform.Modules.Apps;

/// <summary>
/// 应用注册模块：`apps` + `app_api_keys`。它是其余模块的前置——所有业务表的隔离轴 `app_id` 由这里产生。
/// </summary>
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

        services.AddScoped<IAppRegistryService, AppRegistryService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<IM2MTokenService, M2MTokenService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, AppsModelContributor>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapAppsEndpoints();
    }
}
