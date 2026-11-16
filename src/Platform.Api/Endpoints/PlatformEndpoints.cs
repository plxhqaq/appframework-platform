using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Platform.Api.Diagnostics;
using Platform.Contracts;
using Platform.Contracts.Dashboard;
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
                async (
                    HttpContext context,
                    IAppPermissionDirectory appPermissions,
                    string? appKey,
                    CancellationToken cancellationToken) =>
                {
                    if (string.IsNullOrWhiteSpace(appKey))
                    {
                        return ApiResults.Ok(context, PermissionCatalog.All);
                    }

                    var app = await appPermissions.ListAsync(appKey.Trim().ToLowerInvariant(), cancellationToken);
                    return ApiResults.Ok(context, PermissionCatalog.All.Concat(app).ToArray());
                })
            .WithName("platform.permissions")
            .WithSummary("权限码目录；带 appKey 时合并该应用声明的权限")
            .AllowAnonymous();

        platform.MapGet(
                "/dashboard",
                async (
                    HttpContext context,
                    SystemMetricsSampler metrics,
                    PlatformModuleRegistry registry,
                    IEnumerable<IDeviceStatistics> deviceStatistics,
                    IEnumerable<IStorageCapacity> storageCapacity,
                    IHostEnvironment hostEnvironment,
                    CancellationToken cancellationToken) =>
                {
                    var system = metrics.Current;
                    var devices = await ResolveDeviceStatisticsAsync(deviceStatistics, cancellationToken);
                    var availableStorage = await ResolveAvailableStorageAsync(storageCapacity, cancellationToken);

                    // TickCount64 自系统开机起单调递增，用它同时给出开机时间与系统运行时长；
                    // 容器内读到的通常是宿主 uptime，正好对应「服务器开机时间」。
                    var serverTime = DateTimeOffset.UtcNow;
                    var systemUptimeMilliseconds = Math.Max(0, Environment.TickCount64);

                    return ApiResults.Ok(context, new PlatformDashboardDto(
                        serverTime,
                        system.StartedAt,
                        (long)Math.Max(0, (serverTime - system.StartedAt).TotalSeconds),
                        serverTime.AddMilliseconds(-systemUptimeMilliseconds),
                        systemUptimeMilliseconds / 1000,
                        new SystemStatusDto(
                            "ok",
                            system.CpuUsagePercent,
                            system.MemoryUsagePercent,
                            system.WorkingSetBytes,
                            system.ManagedMemoryBytes,
                            system.TotalMemoryBytes,
                            availableStorage,
                            Environment.ProcessorCount,
                            system.ServerCpuUsagePercent,
                            system.ServerMemoryUsagePercent,
                            system.ServerTotalMemoryBytes,
                            system.ServerAvailableMemoryBytes),
                        new DeviceStatisticsDto(
                            devices.Total,
                            devices.Online,
                            devices.Idle,
                            devices.Running,
                            devices.Offline,
                            devices.Disabled,
                            devices.Connected),
                        new PlatformInfoDto(
                            Environment.MachineName,
                            RuntimeInformation.OSDescription,
                            RuntimeInformation.OSArchitecture.ToString(),
                            RuntimeInformation.FrameworkDescription,
                            hostEnvironment.EnvironmentName,
                            ResolvePlatformVersion(),
                            registry.Modules.Count)));
                })
            .WithName("platform.dashboard")
            .WithSummary("总览仪表盘：系统状态、运行平台、资源占用、设备统计与平台信息")
            .RequireAuthorization();

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

    private static async Task<DeviceStatisticsSnapshot> ResolveDeviceStatisticsAsync(
        IEnumerable<IDeviceStatistics> sources,
        CancellationToken cancellationToken)
    {
        var source = sources.FirstOrDefault();
        return source is null
            ? new DeviceStatisticsSnapshot(0, 0, 0, 0, 0, 0, 0)
            : await source.GetAsync(cancellationToken);
    }

    private static async Task<long?> ResolveAvailableStorageAsync(
        IEnumerable<IStorageCapacity> sources,
        CancellationToken cancellationToken)
    {
        var source = sources.FirstOrDefault();
        if (source is null)
        {
            return null;
        }

        var bytes = await source.GetAvailableFreeBytesAsync(cancellationToken);
        return bytes == long.MaxValue ? null : bytes;
    }

    private static string ResolvePlatformVersion()
    {
        var assembly = typeof(PlatformEndpoints).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.0.0";
        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? version[..metadataIndex] : version;
    }
}
