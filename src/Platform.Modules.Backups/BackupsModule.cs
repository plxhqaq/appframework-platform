using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Backups.Endpoints;
using Platform.Modules.Backups.Options;
using Platform.Modules.Backups.Persistence;
using Platform.Modules.Backups.Services;

namespace Platform.Modules.Backups;

/// <summary>
/// 备份模块：命名空间备份（内联/转存）、兼容矩阵、下发编排、保留策略。
/// </summary>
/// <remarks>
/// 落地三条硬约束：① ≤64KB 内联、超阈值转存文件；② 临时文件 → 库提交 → rename 发布；
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
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<BackupsOptions>(configuration.GetSection(BackupsOptions.SectionName));

        services.AddScoped<IBackupNamespaceService, BackupNamespaceService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IBackupPushService, BackupPushService>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, BackupsModelContributor>());

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRetentionCleanupTask, BackupCleanupTask>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapBackupEndpoints();
    }
}
