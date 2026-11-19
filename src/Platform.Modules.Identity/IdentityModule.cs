using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Identity.Endpoints;
using Platform.Modules.Identity.Options;
using Platform.Modules.Identity.Persistence;
using Platform.Modules.Identity.Security;
using Platform.Modules.Identity.Services;

namespace Platform.Modules.Identity;

/// <summary>
/// 身份模块：用户 / 角色 / 权限、用户名口令登录、刷新令牌、会话管理、单次密钥与扫码登录。
/// </summary>
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
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<Argon2Options>(configuration.GetSection(Argon2Options.SectionName));
        services.Configure<LoginThrottleOptions>(configuration.GetSection(LoginThrottleOptions.SectionName));
        services.Configure<IdentityTokenOptions>(configuration.GetSection(IdentityTokenOptions.SectionName));
        services.Configure<IdentitySeedOptions>(configuration.GetSection(IdentitySeedOptions.SectionName));
        services.Configure<SingleLoginOptions>(configuration.GetSection(SingleLoginOptions.SectionName));
        services.Configure<QrLoginOptions>(configuration.GetSection(QrLoginOptions.SectionName));
        services.Configure<IdentityCleanupOptions>(configuration.GetSection(IdentityCleanupOptions.SectionName));

        services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<ILoginThrottleService, LoginThrottleService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IUserTokenEpochDirectory, UserTokenEpochDirectory>();
        services.AddScoped<ISessionRevocationNotifier, SessionRevocationNotifier>();
        services.AddScoped<IUserSessionFactory, UserSessionFactory>();
        services.AddScoped<IIdentityAuthService, IdentityAuthService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<ITemporarySessionFactory, TemporarySessionFactory>();
        services.AddScoped<ISingleLoginKeyService, SingleLoginKeyService>();
        services.AddScoped<IQrLoginService, QrLoginService>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, IdentityModelContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRetentionCleanupTask, IdentityCleanupTask>());
        // 除了开机自启，种子器还要能被 `--reset-admin-password` 那条一次性入口直接解析出来，
        // 而 AddHostedService<T>() 只登记 IHostedService、不登记 T 本身，所以先注册具体单例再转发。
        services.AddSingleton<IdentitySeeder>();
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<IdentitySeeder>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapIdentityEndpoints();
        api.MapAdminAuthEndpoints();
        api.MapIdentityAdminEndpoints();
        api.MapSingleLoginEndpoints();
        api.MapSingleLoginAdminEndpoints();
    }
}
