using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Documents.Endpoints;
using Platform.Modules.Documents.Options;
using Platform.Modules.Documents.Persistence;
using Platform.Modules.Documents.Services;

namespace Platform.Modules.Documents;

/// <summary>
/// 文档模块：集合 / 修订历史 / 服务端强制可见性 / 下载。
/// </summary>
/// <remarks>
/// 客户端清单接口只返回 <c>visibility=client</c> 的集合，**不接受集合参数越权**；
/// 下载支持 Range + ETag + RFC 5987 中文文件名，且客户端下载会再做一次可见性校验。
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
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DocumentsOptions>(configuration.GetSection(DocumentsOptions.SectionName));

        services.AddScoped<IDocumentCollectionService, DocumentCollectionService>();
        services.AddScoped<IDocumentService, DocumentService>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, DocumentsModelContributor>());

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRetentionCleanupTask, DocumentsCleanupTask>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapDocumentEndpoints();
    }
}
