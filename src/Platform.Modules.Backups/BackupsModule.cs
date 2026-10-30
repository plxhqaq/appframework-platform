using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.Modules.Backups;

/// <summary>
/// 备份模块：命名空间备份（内联/转存）、兼容矩阵、下发编排、保留策略。
/// </summary>
/// <remarks>
/// **S0 状态**：只装配骨架与自述端点。S5 落地时保留现网三条硬约束：
/// ① ≤64KB 内联、超阈值转存文件；② 临时文件 → 库提交 → rename 发布；
/// ③ 软删除 + 后台先删文件再删行。兼容矩阵：同渠道硬门禁 + 主版本兼容（<c>force</c> 只放宽主版本）。
/// </remarks>
public sealed class BackupsModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Backups;

    /// <inheritdoc />
    public string DisplayNameKey => "module.backups";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.BackupNamespaceRead,
        PermissionCatalog.BackupNamespaceManage,
        PermissionCatalog.BackupRead,
        PermissionCatalog.BackupCreate,
        PermissionCatalog.BackupPush,
        PermissionCatalog.BackupDelete,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        // S5：备份存储（内联/转存）、兼容性校验、下发编排（需 Hub 上下文，放 Api 装配）。
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api) => api.MapModuleInfo(this);
}
