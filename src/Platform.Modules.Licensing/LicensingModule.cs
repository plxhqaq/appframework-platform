using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Licensing;

/// <summary>
/// 授权模块：产品 / 授权主体（机器码·用户·席位）/ 在线 ECDSA 签名快照 / 离线授权码 / 密钥轮换。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。S4 落地时沿用现网已验证的
/// <c>LicenseToken</c> 快照结构与 <c>OfflineLicenseCodec</c>（Base32-Crockford + 校验位 + 机器码绑定），
/// 私钥只从环境变量/KMS 读取、永不入库；未配置签名密钥时返回 <c>Unmanaged</c>（不限制），避免升级即停产。
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
        // S4：签名服务（ECDSA P-256）、授权仓储、离线码编解码、吊销推送。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
