using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Workers.Maintenance;
using Platform.Workers.Retention;

namespace Platform.Workers.DependencyInjection;

/// <summary>后台任务注册。宿主只需调用一次。</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>注册平台后台维护任务。</summary>
    public static IServiceCollection AddPlatformWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.SectionName));
        services.AddHostedService<MaintenanceWorker>();

        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddHostedService<RetentionWorker>();

        return services;
    }
}
