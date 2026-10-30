using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Modules;
using Platform.Modules.Licensing.Endpoints;
using Platform.Modules.Licensing.Options;
using Platform.Modules.Licensing.Persistence;
using Platform.Modules.Licensing.Services;

namespace Platform.Modules.Licensing;

/// <summary>
/// 授权模块：产品 / 授权主体 / 在线签名快照 / 离线授权码 / 签名密钥轮换。
/// </summary>
/// <remarks>
/// 签名使用 ECDSA P-256，私钥只从部署配置或环境变量读取（<c>Licensing__SigningKeyPkcs8</c>），
/// 数据库只登记公钥与状态；未配置私钥时整个模块返回 <c>unmanaged</c>，不签发任何凭据。
/// 吊销通过 SignalR 推送"变了"的提示，客户端收到后必须回拉状态接口重新校验。
/// </remarks>
public sealed class LicensingModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Licensing;

    /// <inheritdoc />
    public string DisplayNameKey => "module.licensing";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.LicenseProductRead,
        PermissionCatalog.LicenseProductManage,
        PermissionCatalog.LicenseRead,
        PermissionCatalog.LicenseIssue,
        PermissionCatalog.LicenseRevoke,
        PermissionCatalog.LicenseOfflineCodeIssue,
        PermissionCatalog.LicenseSigningKeyManage,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LicensingOptions>(configuration.GetSection(LicensingOptions.SectionName));

        services.AddScoped<ILicenseKeyService, LicenseKeyService>();
        services.AddScoped<ILicenseProductService, LicenseProductService>();
        services.AddScoped<ILicenseEntitlementService, LicenseEntitlementService>();
        services.AddScoped<ILicenseStatusService, LicenseStatusService>();
        services.AddScoped<IOfflineLicenseCodeService, OfflineLicenseCodeService>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, LicensingModelContributor>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapLicensingEndpoints();
    }
}
