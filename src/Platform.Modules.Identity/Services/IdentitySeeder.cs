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
/// 口令**只在新建账号时写入**——现场改过口令以后，重启不该把它盖回去；要按环境变量里的
/// 引导口令强行改回来走 <see cref="ResetAdminPasswordAsync"/>（宿主的 <c>--reset-admin-password</c>）。
/// </remarks>
public sealed class IdentitySeeder : IHostedService
{
    private const string EngineerDisplayName = "现场工程师";

    private const string LegacyEngineerDisplayName = "工程师（临时登录）";

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

    private static readonly Action<ILogger, string, int, Exception?> AdminPasswordResetLog =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(4, "IdentityAdminPasswordReset"),
            "管理员 {UserName} 的口令已按 Seed:AdminPassword 重置，下次登录仍要求改密；"
            + "同时吊销 {Revoked} 个刷新令牌。");

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

        var appAdmin = await EnsureRoleAsync(
            db,
            IdentityConventions.AppAdminRole,
            "应用管理员",
            [
                PermissionCatalog.AppsRead,
                PermissionCatalog.AppsManage,
                PermissionCatalog.AppsApiKeyManage,
                PermissionCatalog.AppsPermissionsManage,
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
                PermissionCatalog.WebhookRead,
                PermissionCatalog.WebhookManage,
                PermissionCatalog.DeviceRead,
                PermissionCatalog.DeviceManage,
                PermissionCatalog.DeviceRestart,
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
                PermissionCatalog.WebhookRead,
                PermissionCatalog.DeviceRead,
            ],
            cancellationToken);

        // 现场工程师角色：给真人账号用，覆盖业务操作与签发密钥/审批扫码入口。
        // 客户端应用自己的权限由各应用清单声明，通过应用级角色授予，平台种子不硬编码。
        var engineer = await EnsureRoleAsync(
            db,
            SingleLoginConventions.EngineerRole,
            EngineerDisplayName,
            [
                PermissionCatalog.AppsRead,
                PermissionCatalog.AppsPermissionsManage,
                PermissionCatalog.IdentitySingleKeyIssue,
                PermissionCatalog.IdentityQrApprove,
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
                PermissionCatalog.DeviceRead,
                PermissionCatalog.DocumentCollectionRead,
                PermissionCatalog.DocumentRead,
                PermissionCatalog.DocumentManage,
                PermissionCatalog.DocumentDelete,
            ],
            cancellationToken);

        UpgradeAppAdminRole(appAdmin);
        UpgradeEngineerRole(engineer);

        await EnsureAdminAsync(db, hasher, platformAdmin, options, clock, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        SeedCompletedLog(_logger, options.DemoAppKey, null);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 把 <c>Seed:AdminPassword</c> 重新按到管理员账号上，并作废它名下全部会话。
    /// </summary>
    /// <remarks>
    /// <see cref="StartAsync"/> 的口令只在账号不存在时写入，这是对的：现场改过口令以后，
    /// 重启不该把它盖回去。但反方向的事故同样常见——<c>.env</c> 被删过或重新生成过而数据卷还是老的，
    /// 部署信息上印的引导口令就对不上库里那把哈希，人直接被挡在门外。找回入口必须是一条显式的、
    /// 跑完即退的动作（<c>--reset-admin-password</c>），而不是「删账号再重启种子」那种会连带丢历史的做法。
    /// </remarks>
    /// <returns>被吊销的刷新令牌数量。</returns>
    public async Task<int> ResetAdminPasswordAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            throw new InvalidOperationException(
                "Seed:AdminPassword（环境变量 Seed__AdminPassword）为空，没有可写入的口令。"
                + "请先在 deploy/.env 里设好 SEED_ADMIN_PASSWORD，或让部署脚本生成一个。");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PlatformDbContext>();
        var hasher = services.GetRequiredService<IPasswordHasher>();
        var clock = services.GetRequiredService<IClock>();
        var refreshTokens = services.GetRequiredService<IRefreshTokenService>();
        var throttle = services.GetRequiredService<ILoginThrottleService>();

        var platformAdmin = await EnsureRoleAsync(
            db,
            IdentityConventions.PlatformAdminRole,
            "平台管理员",
            [PermissionCatalog.Wildcard],
            cancellationToken);

        // 上面已确认口令非空，这里不会走到「跳过创建」那一支。
        var admin = (await EnsureAdminAsync(
            db, hasher, platformAdmin, options, clock, cancellationToken,
            overwritePassword: true))!;

        await db.SaveChangesAsync(cancellationToken);

        var revoked = await refreshTokens.RevokeAllAsync(
            admin.Id,
            RefreshTokenRevokeReasons.PasswordChanged,
            cancellationToken);

        // 撞口令的那几次多半已经把账号锁上了；口令既然已经找回，不该再让人白等一刻钟。
        await throttle.RegisterSuccessAsync(admin.UserName, cancellationToken);

        AdminPasswordResetLog(_logger, admin.UserName, revoked, null);
        return revoked;
    }

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

    /// <summary>
    /// 老库里的工程师角色只覆盖业务权限，签不了密钥也审批不了扫码，真人账号登录后两个页面都是空的。
    /// 这里只**补齐**缺的入口权限，不整体覆盖集合——管理员手工调过的其余部分要留着。
    /// </summary>
    /// <summary>
    /// 老库内置角色已经存在时 <see cref="EnsureRoleAsync"/> 不会覆盖权限；
    /// 新增的敏感入口权限只在种子期补齐，管理员手工调整过的其余集合保持不变。
    /// </summary>
    private static void UpgradeAppAdminRole(Role appAdmin)
    {
        if (!appAdmin.Permissions.Contains(PermissionCatalog.AppsPermissionsManage, StringComparer.Ordinal))
        {
            appAdmin.Permissions = [.. appAdmin.Permissions, PermissionCatalog.AppsPermissionsManage];
        }
    }

    private static void UpgradeEngineerRole(Role engineer)
    {
        var missing = new[]
            {
                PermissionCatalog.AppsPermissionsManage,
                PermissionCatalog.IdentitySingleKeyIssue,
                PermissionCatalog.IdentityQrApprove,
                // 下发必须先选设备：留空不再广播，缺 device.read 会让备份下发整页不可用。
                PermissionCatalog.DeviceRead,
            }
            .Except(engineer.Permissions, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            engineer.Permissions = [.. engineer.Permissions, .. missing];
        }

        if (string.Equals(engineer.DisplayName, LegacyEngineerDisplayName, StringComparison.Ordinal))
        {
            engineer.DisplayName = EngineerDisplayName;
        }
    }

    /// <summary>
    /// 保证管理员存在并挂在平台管理员角色上。口令只在**新建**时写入；
    /// <paramref name="overwritePassword"/> 是找回入口专用的显式覆盖。
    /// 口令为空且账号尚不存在时返回 <c>null</c>（跳过创建）。
    /// </summary>
    private async Task<User?> EnsureAdminAsync(
        PlatformDbContext db,
        IPasswordHasher hasher,
        Role platformAdmin,
        IdentitySeedOptions options,
        IClock clock,
        CancellationToken cancellationToken,
        bool overwritePassword = false)
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
                return null;
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
        else if (overwritePassword)
        {
            PasswordPolicy.EnsureValid(options.AdminPassword);
            admin.PasswordHash = hasher.Hash(options.AdminPassword!).Encoded;
            admin.IsEnabled = true;
            admin.MustChangePassword = true;
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

        return admin;
    }
}
