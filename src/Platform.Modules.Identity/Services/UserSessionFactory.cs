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

    /// <summary>构造。</summary>
    public UserSessionFactory(PlatformDbContext db, IAppDirectory apps)
    {
        _db = db;
        _apps = apps;
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

        var roleNames = roles.Select(static x => x.Name).ToList();

        var wildcard = roles.Any(
            static role => role.Permissions.Contains(PermissionCatalog.Wildcard, StringComparer.Ordinal));

        IReadOnlyList<string> permissions;
        if (wildcard)
        {
            permissions = [PermissionCatalog.Wildcard];
        }
        else
        {
            permissions = roles
                .SelectMany(static role => role.Permissions)
                .Where(PermissionCatalog.IsKnown)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        var appKeys = new List<string>();
        var appScopedKeys = new List<string>();
        if (wildcard)
        {
            appKeys.AddRange(await _apps.ListEnabledKeysAsync(cancellationToken));
        }

        foreach (var role in roles)
        {
            if (string.Equals(role.Scope, IdentityConventions.ScopeApp, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(role.AppKey))
            {
                appScopedKeys.Add(role.AppKey);
                appKeys.Add(role.AppKey);
            }
        }

        var distinctAppKeys = appKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var appKey = ResolveAppKey(distinctAppKeys, appScopedKeys, requestedAppKey);

        var session = new UserSessionDto(
            user.Id,
            user.UserName,
            user.DisplayName,
            roleNames,
            permissions,
            distinctAppKeys,
            user.MustChangePassword,
            appKey);

        return new UserSessionContext(session, appKey);
    }

    private static string? ResolveAppKey(
        IReadOnlyList<string> granted,
        List<string> appScoped,
        string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            // 平台管理员不主动进入某个应用时，令牌不带"当前应用"，但仍可访问全部启用应用。
            return appScoped.Count > 0 ? appScoped[0] : null;
        }

        var appKey = requested.Trim();
        if (granted.Contains(appKey, StringComparer.Ordinal))
        {
            return appKey;
        }

        throw new BusinessException(
            ApiErrorCode.IdentityPermissionDenied,
            $"当前用户无权进入应用 {appKey}。");
    }
}
