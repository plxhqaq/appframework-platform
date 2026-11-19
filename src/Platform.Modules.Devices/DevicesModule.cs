using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Devices.Endpoints;
using Platform.Modules.Devices.Hubs;
using Platform.Modules.Devices.Options;
using Platform.Modules.Devices.Persistence;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices;

/// <summary>
/// 设备接入域：节点（物理机）+ 安装实例（appKey + installGuid）注册/心跳/工位与管理端查询。
/// </summary>
/// <remarks>
/// 注册/心跳通过 <c>/hubs/devices</c> 提供；本模块维护节点与安装实例两份事实，
/// 其它模块需要设备信息时走 Kernel 窄接口，不引用本模块实体。
/// </remarks>
public sealed class DevicesModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Devices;

    /// <inheritdoc />
    public string DisplayNameKey => "module.devices";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.InstallationRead,
        PermissionCatalog.InstallationManage,
        PermissionCatalog.InstallationRestart,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DevicesOptions>(configuration.GetSection(DevicesOptions.SectionName));

        services.AddScoped<IInstallationRegistry, InstallationRegistry>();
        services.AddScoped<IInstallationPresence, InstallationPresence>();
        services.AddScoped<IInstallationStateNotifier, InstallationStateNotifier>();
        services.AddScoped<INodeAdmin, NodeAdmin>();
        services.AddScoped<IInstallationAdmin, InstallationAdmin>();
        services.AddScoped<IInstallationDirectory, InstallationDirectory>();
        services.AddScoped<IDeviceNodeDirectory, DeviceNodeDirectory>();
        services.AddScoped<IInstallationCommandGateway, InstallationCommandGateway>();
        services.AddScoped<IInstallationEventRouter, InstallationEventRouter>();
        services.AddScoped<IInventoryStatistics, InventoryStatisticsService>();
        services.AddSingleton<InstallationConnectionRegistry>();
        services.AddSingleton<InstallationHeartbeatTracker>();
        services.AddSingleton<IInstallationCommandAcks, InstallationCommandAcks>();
        services.AddSingleton<DeviceHubGuardFilter>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, DevicesModelContributor>());

        services.AddHostedService<InstallationOfflineWorker>();
        services.AddHostedService<HeartbeatFlushWorker>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapNodeEndpoints();
        api.MapInstallationEndpoints();
    }
}
