using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Identity;

/// <summary>
/// 身份模块：用户 / 角色 / 权限、用户名口令登录、6 位单次密钥登录、扫码登录、刷新令牌。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。S2 起按现网语义搬运单次密钥原子消费
/// （<c>ExecuteUpdateAsync</c> + WHERE 条件，16 并发只允许 1 次成功）与扫码会话
/// （DB 会话 + SignalR 推送 + 轮询兜底，解决重启丢会话）。
/// </remarks>
public sealed class IdentityModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Identity;

    /// <inheritdoc />
    public string DisplayNameKey => "module.identity";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.IdentityUserRead,
        PermissionCatalog.IdentityUserManage,
        PermissionCatalog.IdentityUserPasswordReset,
        PermissionCatalog.IdentityRoleRead,
        PermissionCatalog.IdentityRoleManage,
        PermissionCatalog.IdentitySessionRead,
        PermissionCatalog.IdentitySessionRevoke,
        PermissionCatalog.IdentitySingleKeyIssue,
        PermissionCatalog.IdentityQrApprove,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        // S1/S2：口令哈希（Argon2 参数族）、令牌签发、单次密钥服务、扫码会话服务、账号级节流。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
