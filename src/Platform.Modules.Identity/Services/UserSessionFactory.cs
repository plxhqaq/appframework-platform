using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Services;

/// <summary>用户会话上下文：令牌签发与登录响应共用同一份角色/权限/应用口径。</summary>
/// <param name="Session">主体信息。</param>
/// <param name="AppKey">本次令牌固定的应用上下文。</param>
public sealed record UserSessionContext(UserSessionDto Session, string? AppKey);

/// <summary>把用户当前的角色与权限解析成会话上下文。</summary>
public interface IUserSessionFactory
{
    /// <summary>构建会话；用户不存在/禁用/无权进入指定应用时抛对应业务错误码。</summary>
    Task<UserSessionContext> BuildAsync(
        Guid userId,
        string? requestedAppKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话工厂。每次登录/刷新都重新读库，因此角色改权限后**下一次刷新即生效**，
/// 不需要等访问令牌自然过期。
/// </summary>
public sealed class UserSessionFactory : IUserSessionFactory
{
    private readonly PlatformDbContext _db;
    private readonly IAppDirectory _apps;
    private readonly IAppPermissionDirectory _appPermissions;

    /// <summary>构造。</summary>
    public UserSessionFactory(
        PlatformDbContext db,
        IAppDirectory apps,
        IAppPermissionDirectory appPermissions)
    {
        _db = db;
        _apps = apps;
        _appPermissions = appPermissions;
    }

    /// <inheritdoc />
    public async Task<UserSessionContext> BuildAsync(
        Guid userId,
        string? requestedAppKey,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Set<User>()
            .AsNoTracking()
            .Include(static x => x.UserRoles)
            .ThenInclude(static x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw BusinessException.NotFound(ApiErrorCode.IdentityUserNotFound, $"用户 {userId} 不存在。");

        if (!user.IsEnabled)
        {
            throw new BusinessException(ApiErrorCode.IdentityUserDisabled, $"用户 {user.UserName} 已被禁用。");
        }

        var roles = user.UserRoles
            .Select(static x => x.Role)
            .Where(static x => x is not null)
            .Select(static x => x!)
            .OrderBy(static x => x.Name)
            .ToList();

        // 应用级角色只在该应用上下文授予权限；平台级角色（含内置通配符）始终生效。
        // 通配符只认平台角色，避免一条脏数据里的 app 角色把权限放大到全局。
        var wildcard = roles.Any(static role => IsPlatformScoped(role)
            && role.Permissions.Contains(PermissionCatalog.Wildcard, StringComparer.Ordinal));

        var appScopedKeys = roles
            .Where(static role => IsAppScoped(role) && !string.IsNullOrWhiteSpace(role.AppKey))
            .Select(static role => role.AppKey!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // 只有平台管理员或存在应用角色的用户才需要查启用应用；结果同时用于会话里的
        // 可切换应用列表和 requestedAppKey 的停用校验。
        IReadOnlyList<string> enabledAppKeys = wildcard || appScopedKeys.Count > 0
            ? await _apps.ListEnabledKeysAsync(cancellationToken)
            : [];
        var enabledAppKeySet = enabledAppKeys.ToHashSet(StringComparer.Ordinal);

        var sessionAppKeys = wildcard
            ? enabledAppKeys
            : appScopedKeys.Where(enabledAppKeySet.Contains).ToList();

        var appKey = ResolveAppKey(
            grantedAppKeys: wildcard ? enabledAppKeys : appScopedKeys,
            enabledAppKeys: enabledAppKeySet,
            appScopedKeys,
            requestedAppKey);

        var effectiveRoles = roles
            .Where(role => IsPlatformScoped(role) || IsCurrentAppRole(role, appKey))
            .ToList();
        var roleNames = effectiveRoles.Select(static x => x.Name).ToList();

        IReadOnlyList<string> permissions;
        if (wildcard)
        {
            permissions = [PermissionCatalog.Wildcard];
        }
        else
        {
            // 平台权限始终有效；应用声明的权限只在该应用上下文里并入令牌。
            var allowed = PermissionCatalog.Codes.ToHashSet(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(appKey))
            {
                allowed.UnionWith(await _appPermissions.ListCodesAsync(appKey, cancellationToken));
            }

            permissions = effectiveRoles
                .SelectMany(static role => role.Permissions)
                .Where(allowed.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        var permissionsLocked = !string.IsNullOrWhiteSpace(appKey)
            && await _appPermissions.IsLockedAsync(appKey, cancellationToken);

        var session = new UserSessionDto(
            user.Id,
            user.UserName,
            user.DisplayName,
            roleNames,
            permissions,
            sessionAppKeys,
            user.MustChangePassword,
            appKey,
            permissionsLocked);

        return new UserSessionContext(session, appKey);
    }

    private static bool IsPlatformScoped(Role role) =>
        !string.Equals(role.Scope, IdentityConventions.ScopeApp, StringComparison.OrdinalIgnoreCase);

    private static bool IsAppScoped(Role role) =>
        string.Equals(role.Scope, IdentityConventions.ScopeApp, StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentAppRole(Role role, string? appKey) =>
        IsAppScoped(role)
        && !string.IsNullOrEmpty(appKey)
        && string.Equals(role.AppKey?.Trim(), appKey, StringComparison.Ordinal);

    private static string? ResolveAppKey(
        IReadOnlyList<string> grantedAppKeys,
        HashSet<string> enabledAppKeys,
        IReadOnlyList<string> appScopedKeys,
        string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            // 未显式指定时优先进入第一个已启用、且用户有 app 角色的应用；
            // 平台管理员没有 app 角色时保持 null，由令牌里的可访问应用列表决定首个 app。
            return appScopedKeys.FirstOrDefault(enabledAppKeys.Contains);
        }

        var appKey = requested.Trim();
        if (!grantedAppKeys.Contains(appKey, StringComparer.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPermissionDenied,
                $"当前用户无权进入应用 {appKey}。");
        }

        if (!enabledAppKeys.Contains(appKey))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPermissionDenied,
                $"应用 {appKey} 已停用，不能再签发该应用的令牌。");
        }

        return appKey;
    }
}
