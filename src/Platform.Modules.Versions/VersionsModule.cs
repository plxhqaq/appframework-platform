using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Versions.Endpoints;
using Platform.Modules.Versions.Ingestion;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Packages;
using Platform.Modules.Versions.Persistence;
using Platform.Modules.Versions.Retention;
using Platform.Modules.Versions.Services;

namespace Platform.Modules.Versions;

/// <summary>
/// 版本模块：渠道 / 版本 / 多平台产物 / 文件级清单 / 下载服务 / 检查更新。
/// </summary>
/// <remarks>
/// 内容按 SHA-256 内容寻址存放；客户端先申请上传票据再直传，清单在发布时校验一致性。
/// 下载走 Range / ETag / 304，按文件流式返回，不把整个包读进内存。
/// </remarks>
public sealed class VersionsModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Versions;

    /// <inheritdoc />
    public string DisplayNameKey => "module.versions";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.VersionsChannelRead,
        PermissionCatalog.VersionsChannelManage,
        PermissionCatalog.VersionsReleaseRead,
        PermissionCatalog.VersionsReleasePublish,
        PermissionCatalog.VersionsReleaseArchive,
        PermissionCatalog.VersionsArtifactUpload,
        PermissionCatalog.VersionsArtifactDownload,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<VersionsOptions>(configuration.GetSection(VersionsOptions.SectionName));

        services.AddScoped<IReleaseChannelService, ReleaseChannelService>();
        services.AddScoped<IReleaseService, ReleaseService>();
        services.AddScoped<IVersionEventsPublisher, VersionEventsPublisher>();
        services.AddScoped<IReleasePackageService, ReleasePackageService>();
        services.AddScoped<IReleasePackageIngestionService, ReleasePackageIngestionService>();
        services.AddScoped<IUpdaterReleaseService, UpdaterReleaseService>();
        services.AddScoped<IUploadTicketService, UploadTicketService>();
        services.AddScoped<IReleaseContentService, ReleaseContentService>();
        services.AddSingleton<IPackageManifestProbe, PackageManifestProbe>();
        // 管理台下载票据用 DataProtection 做限时签名，无需落库；zip 缓存生成器无状态。
        services.AddDataProtection();
        services.AddSingleton<IDownloadTicketService, DownloadTicketService>();
        services.AddSingleton<IDownloadArchiveService, DownloadArchiveService>();
        services.AddHostedService<ReleasePackageIngestionWorker>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, VersionsModelContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRetentionCleanupTask, VersionsRetentionTask>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapVersionsEndpoints();
    }
}
