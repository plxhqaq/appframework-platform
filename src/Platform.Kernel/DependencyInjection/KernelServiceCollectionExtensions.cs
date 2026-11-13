using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Diagnostics;
using Platform.Kernel.Hubs;
using Platform.Kernel.Metrics;
using Platform.Kernel.Security;

namespace Platform.Kernel.DependencyInjection;

/// <summary>内核服务注册。宿主只需调用一次。</summary>
public static class KernelServiceCollectionExtensions
{
    /// <summary>
    /// 注册内核横切能力：时钟、当前主体、权限策略与处理器。
    /// </summary>
    /// <remarks>
    /// 认证方案的注册（JWT / API Key）不在这里，由宿主决定；内核只消费 <c>HttpContext.User</c>。
    /// </remarks>
    public static IServiceCollection AddPlatformKernel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<ICurrentPrincipal, HttpCurrentPrincipal>();
        services.TryAddSingleton<IPlatformPushCoordinator, PlatformPushCoordinator>();
        services.TryAddScoped<IPlatformEventPublisher, NullPlatformEventPublisher>();
        services.TryAddSingleton<PlatformMetrics>();
        services.TryAddSingleton(sp => new AppStateCache(
            sp.GetRequiredService<IClock>(),
            TimeSpan.FromSeconds(Math.Max(
                0,
                sp.GetRequiredService<IConfiguration>().GetValue("AppContextGuard:CacheSeconds", 30)))));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformMetricSource, ProcessMetricSource>());

        services.AddAuthorization();
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAuthorizationHandler, PermissionAuthorizationHandler>());

        return services;
    }
}
