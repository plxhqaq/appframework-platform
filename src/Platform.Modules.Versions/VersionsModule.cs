using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Versions;

/// <summary>
/// 版本模块：渠道 / 版本 / 多平台产物 / 文件级清单 / 下载服务 / 检查更新。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。S3 落地：upload ticket 直传、清单生成与校验、
/// 下载服务器（Range / ETag=sha256 / If-None-Match 304 / 流式不分片进内存）、
/// 以及客户端侧「文件级 content-addressed 差异」所需的清单接口。
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
        // S3：通道/版本仓储、清单校验器、上传票据、下载流式服务。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
