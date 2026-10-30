using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Services;

/// <summary>角色管理（管理端）。</summary>
public interface IRoleAdminService
{
    /// <summary>全部角色（管理端角色数量有限，不分页）。</summary>
    Task<IReadOnlyList<RoleSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>角色详情。</summary>
    Task<RoleSummaryDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建角色。</summary>
    Task<RoleSummaryDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>更新显示名/作用域/权限；字段为空表示不修改。</summary>
    Task<RoleSummaryDto> UpdateAsync(
        Guid id,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除角色（内置角色与使用中的角色不可删除）。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>角色管理实现。</summary>
public sealed partial class RoleAdminService : IRoleAdminService
{
    private readonly PlatformDbContext _db;
    private readonly IAppDirectory _apps;

    /// <summary>构造。</summary>
    public RoleAdminService(PlatformDbContext db, IAppDirectory apps)
    {
        _db = db;
        _apps = apps;
    }

    [GeneratedRegex(IdentityConventions.RoleNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex RoleNameRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _db.Set<Role>()
            .AsNoTracking()
            .Include(static x => x.UserRoles)
            .OrderBy(static x => x.Name)
            .ToListAsync(cancellationToken);

        return [.. roles.Select(ToSummary)];
    }

    /// <inheritdoc />
    public async Task<RoleSummaryDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var role = await LoadAsync(id, cancellationToken);
        return ToSummary(role);
    }

    /// <inheritdoc />
    public async Task<RoleSummaryDto> CreateAsync(
        CreateRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = NormalizeName(request.Name);
        var scope = IdentityRoleScopes.Normalize(request.Scope);
        if (!IdentityRoleScopes.IsValid(scope))
        {
            throw BusinessException.Invalid("角色作用域只能是 platform 或 app。");
        }

        var appKey = await ResolveAppKeyAsync(scope, request.AppKey, cancellationToken);
        var permissions = NormalizePermissions(request.Permissions, name);

        if (await _db.Set<Role>().AnyAsync(x => x.Name == name, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.Conflict, $"角色 {name} 已存在。");
        }

        var role = new Role
        {
            Name = name,
            DisplayName = NormalizeDisplayName(request.DisplayName),
            Scope = scope,
            AppKey = appKey,
            Permissions = permissions,
        };

        _db.Set<Role>().Add(role);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(ApiErrorCode.Conflict, $"角色 {name} 已存在。", exception);
        }

        return ToSummary(role);
    }

    /// <inheritdoc />
    public async Task<RoleSummaryDto> UpdateAsync(
        Guid id,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = await LoadAsync(id, cancellationToken);
        var isPlatformAdmin = string.Equals(role.Name, IdentityConventions.PlatformAdminRole, StringComparison.Ordinal);

        if (request.DisplayName is not null)
        {
            role.DisplayName = NormalizeDisplayName(request.DisplayName);
        }

        if (request.Scope is not null)
        {
            var scope = IdentityRoleScopes.Normalize(request.Scope);
            if (!IdentityRoleScopes.IsValid(scope))
            {
                throw BusinessException.Invalid("角色作用域只能是 platform 或 app。");
            }

            if (isPlatformAdmin && !string.Equals(scope, IdentityConventions.ScopePlatform, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.IdentityPermissionDenied,
                    "内置平台管理员角色必须保持 platform 作用域。");
            }

            role.Scope = scope;
        }

        if (request.Scope is not null || request.AppKey is not null)
        {
            role.AppKey = await ResolveAppKeyAsync(role.Scope, request.AppKey ?? role.AppKey, cancellationToken);
        }

        if (request.Permissions is not null)
        {
            var permissions = NormalizePermissions(request.Permissions, role.Name);
            if (isPlatformAdmin && !permissions.Contains(PermissionCatalog.Wildcard, StringComparer.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.IdentityPermissionDenied,
                    "内置平台管理员角色必须保留通配符权限。");
            }

            role.Permissions = permissions;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var role = await LoadAsync(id, cancellationToken);

        if (role.IsSystem)
        {
            throw new BusinessException(ApiErrorCode.Conflict, "内置角色不可删除。");
        }

        if (await _db.Set<UserRole>().AnyAsync(x => x.RoleId == id, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.IdentityRoleInUse, "该角色下仍有用户，无法删除。");
        }

        _db.Set<Role>().Remove(role);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Role> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Set<Role>()
            .Include(static x => x.UserRoles)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw BusinessException.NotFound(ApiErrorCode.IdentityRoleNotFound, $"角色 {id} 不存在。");

    private async Task<string?> ResolveAppKeyAsync(
        string scope,
        string? appKey,
        CancellationToken cancellationToken)
    {
        if (string.Equals(scope, IdentityConventions.ScopePlatform, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw BusinessException.Invalid("应用级角色必须指定应用键。");
        }

        var normalized = appKey.Trim().ToLowerInvariant();
        if (!await _apps.ExistsAsync(normalized, cancellationToken))
        {
            throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {normalized} 不存在。");
        }

        return normalized;
    }

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RoleNameRegex().IsMatch(normalized))
        {
            throw BusinessException.Invalid("角色名必须为 2–64 位小写字母、数字或连字符，且以字母开头。");
        }

        return normalized;
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var trimmed = displayName.Trim();
        if (trimmed.Length > 256)
        {
            throw BusinessException.Invalid("角色显示名不得超过 256 个字符。");
        }

        return trimmed;
    }

    private static List<string> NormalizePermissions(IReadOnlyList<string>? permissions, string roleName)
    {
        if (permissions is null || permissions.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        var containsWildcard = false;
        foreach (var raw in permissions)
        {
            var code = raw?.Trim();
            if (string.IsNullOrEmpty(code) || result.Contains(code, StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(code, PermissionCatalog.Wildcard, StringComparison.Ordinal))
            {
                containsWildcard = true;
                continue;
            }

            if (!PermissionCatalog.IsKnown(code))
            {
                throw new BusinessException(
                    ApiErrorCode.IdentityPermissionUnknown,
                    $"未知权限码：{code}。");
            }

            result.Add(code);
        }

        if (containsWildcard)
        {
            if (!string.Equals(roleName, IdentityConventions.PlatformAdminRole, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.IdentityPermissionDenied,
                    "只有内置平台管理员角色可以使用通配符权限。");
            }

            return [PermissionCatalog.Wildcard];
        }

        return result;
    }

    private static RoleSummaryDto ToSummary(Role role) =>
        new(
            role.Id,
            role.Name,
            role.DisplayName,
            role.Scope,
            role.AppKey,
            role.Permissions,
            role.IsSystem,
            role.UserRoles.Count,
            role.CreatedAt);
}
