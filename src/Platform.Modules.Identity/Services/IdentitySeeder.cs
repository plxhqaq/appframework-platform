using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;
using Platform.Modules.Identity.Security;

namespace Platform.Modules.Identity.Services;

/// <summary>
/// 幂等种子：内置角色、演示应用与管理员账号。
/// </summary>
/// <remarks>
/// 在宿主开始接收请求前同步执行（<see cref="IHostedService.StartAsync"/>），
/// 因此不会出现"首个请求打到空表"的窗口。管理员初始口令只来自
/// <c>Seed__AdminPassword</c> 环境变量；为空时跳过管理员创建并记警告。
/// </remarks>
public sealed class IdentitySeeder : IHostedService
{
    private static readonly Action<ILogger, Exception?> SeedDisabledLog = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1, "IdentitySeedDisabled"),
        "身份种子已关闭（Seed:Enabled=false）。");

    private static readonly Action<ILogger, string, Exception?> SeedCompletedLog = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(2, "IdentitySeedCompleted"),
        "身份种子完成：演示应用 {AppKey}、内置角色与管理员账号已就绪。");

    private static readonly Action<ILogger, string, Exception?> AdminPasswordMissingLog = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(3, "IdentitySeedAdminPasswordMissing"),
        "未配置 Seed:AdminPassword（环境变量 Seed__AdminPassword），已跳过管理员 {UserName} 创建；"
        + "内置角色与演示应用已就绪。");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<IdentitySeedOptions> _options;
    private readonly ILogger<IdentitySeeder> _logger;

    /// <summary>构造。</summary>
    public IdentitySeeder(
        IServiceScopeFactory scopeFactory,
        IOptions<IdentitySeedOptions> options,
        ILogger<IdentitySeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            SeedDisabledLog(_logger, null);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PlatformDbContext>();
        var apps = services.GetRequiredService<IAppDirectory>();
        var hasher = services.GetRequiredService<IPasswordHasher>();
        var clock = services.GetRequiredService<IClock>();

        await apps.EnsureAsync(options.DemoAppKey, options.DemoAppName, cancellationToken);

        var platformAdmin = await EnsureRoleAsync(
            db,
            IdentityConventions.PlatformAdminRole,
            "平台管理员",
            [PermissionCatalog.Wildcard],
            cancellationToken);

        await EnsureRoleAsync(
            db,
            IdentityConventions.AppAdminRole,
            "应用管理员",
            [
                PermissionCatalog.AppsRead,
                PermissionCatalog.AppsManage,
                PermissionCatalog.AppsApiKeyManage,
                PermissionCatalog.IdentityUserRead,
                PermissionCatalog.IdentityRoleRead,
                PermissionCatalog.VersionsChannelRead,
                PermissionCatalog.VersionsChannelManage,
                PermissionCatalog.VersionsReleaseRead,
                PermissionCatalog.VersionsReleasePublish,
                PermissionCatalog.VersionsReleaseArchive,
                PermissionCatalog.VersionsArtifactUpload,
                PermissionCatalog.VersionsArtifactDownload,
                PermissionCatalog.LicenseProductRead,
                PermissionCatalog.LicenseRead,
                PermissionCatalog.LicenseIssue,
                PermissionCatalog.LicenseRevoke,
                PermissionCatalog.BackupNamespaceRead,
                PermissionCatalog.BackupRead,
                PermissionCatalog.BackupCreate,
                PermissionCatalog.BackupPush,
                PermissionCatalog.DocumentCollectionRead,
                PermissionCatalog.DocumentRead,
                PermissionCatalog.DocumentManage,
            ],
            cancellationToken);

        await EnsureRoleAsync(
            db,
            IdentityConventions.ViewerRole,
            "只读观察员",
            [
                PermissionCatalog.AppsRead,
                PermissionCatalog.IdentityUserRead,
                PermissionCatalog.IdentityRoleRead,
                PermissionCatalog.VersionsChannelRead,
                PermissionCatalog.VersionsReleaseRead,
                PermissionCatalog.LicenseProductRead,
                PermissionCatalog.LicenseRead,
                PermissionCatalog.BackupNamespaceRead,
                PermissionCatalog.BackupRead,
                PermissionCatalog.DocumentCollectionRead,
                PermissionCatalog.DocumentRead,
            ],
            cancellationToken);

        // 单次密钥/扫码登录签发的临时令牌直接以此角色取权限：只覆盖业务操作与客户端功能，
        // 不含用户/角色/权限维护，也不含签发新密钥与审批扫码，避免临时会话自我续期。
        await EnsureRoleAsync(
            db,
            SingleLoginConventions.EngineerRole,
            "工程师（临时登录）",
            [
                PermissionCatalog.AppsRead,
                PermissionCatalog.VersionsChannelRead,
                PermissionCatalog.VersionsReleaseRead,
                PermissionCatalog.VersionsArtifactDownload,
                PermissionCatalog.LicenseProductRead,
                PermissionCatalog.LicenseRead,
                PermissionCatalog.BackupNamespaceRead,
                PermissionCatalog.BackupRead,
                PermissionCatalog.BackupCreate,
                PermissionCatalog.BackupPush,
                PermissionCatalog.BackupDelete,
                PermissionCatalog.DocumentCollectionRead,
                PermissionCatalog.DocumentRead,
                PermissionCatalog.DocumentManage,
                PermissionCatalog.DocumentDelete,
                .. ClientPermissionCatalog.Permissions.Select(static p => p.Code),
            ],
            cancellationToken);

        await EnsureAdminAsync(db, hasher, platformAdmin, options, clock, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        SeedCompletedLog(_logger, options.DemoAppKey, null);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<Role> EnsureRoleAsync(
        PlatformDbContext db,
        string name,
        string displayName,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        var role = await db.Set<Role>().FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
        if (role is null)
        {
            role = new Role
            {
                Name = name,
                DisplayName = displayName,
                Scope = IdentityConventions.ScopePlatform,
                Permissions = [.. permissions],
                IsSystem = true,
            };
            db.Set<Role>().Add(role);
            return role;
        }

        role.IsSystem = true;
        role.Scope = IdentityConventions.ScopePlatform;
        role.AppKey = null;
        if (string.Equals(name, IdentityConventions.PlatformAdminRole, StringComparison.Ordinal))
        {
            role.Permissions = [PermissionCatalog.Wildcard];
        }

        return role;
    }

    private async Task EnsureAdminAsync(
        PlatformDbContext db,
        IPasswordHasher hasher,
        Role platformAdmin,
        IdentitySeedOptions options,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var userName = string.IsNullOrWhiteSpace(options.AdminUserName)
            ? IdentityConventions.AdminUserName
            : options.AdminUserName.Trim().ToLowerInvariant();

        var admin = await db.Set<User>()
            .Include(static x => x.UserRoles)
            .FirstOrDefaultAsync(x => x.UserName == userName, cancellationToken);

        if (admin is null)
        {
            if (string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                AdminPasswordMissingLog(_logger, userName, null);
                return;
            }

            PasswordPolicy.EnsureValid(options.AdminPassword);
            admin = new User
            {
                UserName = userName,
                DisplayName = options.AdminDisplayName,
                PasswordHash = hasher.Hash(options.AdminPassword).Encoded,
                IsEnabled = true,
                MustChangePassword = true,
            };
            db.Set<User>().Add(admin);
        }

        if (admin.UserRoles.All(x => x.RoleId != platformAdmin.Id))
        {
            admin.UserRoles.Add(new UserRole
            {
                User = admin,
                Role = platformAdmin,
                RoleId = platformAdmin.Id,
                CreatedAt = clock.UtcNow,
            });
        }
    }
}
