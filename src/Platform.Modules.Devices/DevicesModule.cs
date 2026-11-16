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
using Platform.Modules.Devices.Options;
using Platform.Modules.Devices.Persistence;
using Platform.Modules.Devices.Services;

namespace Platform.Modules.Devices;

/// <summary>
/// 通用设备域：设备注册/心跳/工位/在线状态与管理端查询。
/// </summary>
/// <remarks>
/// 设备注册/心跳后续通过 <c>/hubs/devices</c> 提供；本模块只维护一份设备事实，
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
        PermissionCatalog.DeviceRead,
        PermissionCatalog.DeviceManage,
        PermissionCatalog.DeviceRestart,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DevicesOptions>(configuration.GetSection(DevicesOptions.SectionName));

        services.AddScoped<IDeviceRegistry, DeviceRegistry>();
        services.AddScoped<IDevicePresence, DevicePresence>();
        services.AddScoped<IDeviceAdmin, DeviceAdmin>();
        services.AddScoped<IDeviceDirectory, DeviceDirectory>();
        services.AddScoped<IDeviceCommandGateway, DeviceCommandGateway>();
        services.AddScoped<IDeviceStatistics, DeviceStatisticsService>();
        services.AddSingleton<DeviceConnectionRegistry>();
        services.AddSingleton<IDeviceCommandAcks, DeviceCommandAcks>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, DevicesModelContributor>());

        services.AddHostedService<DeviceOfflineWorker>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapDeviceEndpoints();
    }
}
