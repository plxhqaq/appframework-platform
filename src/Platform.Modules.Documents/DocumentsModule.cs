using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Documents;

/// <summary>
/// 文档模块：集合 / 修订历史 / 服务端强制可见性 / 下载。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。S5 落地时：
/// 客户端清单接口只返回 <c>visibility=client</c> 的集合，**不接受集合参数越权**
/// （现网 <c>ClientUsage</c> 硬过滤的推广）；下载支持 Range + ETag + RFC 5987 中文文件名。
/// </remarks>
public sealed class DocumentsModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Documents;

    /// <inheritdoc />
    public string DisplayNameKey => "module.documents";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.DocumentCollectionRead,
        PermissionCatalog.DocumentCollectionManage,
        PermissionCatalog.DocumentRead,
        PermissionCatalog.DocumentManage,
        PermissionCatalog.DocumentDelete,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        // S5：集合/文档/修订仓储、可见性过滤、下载流式服务。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
