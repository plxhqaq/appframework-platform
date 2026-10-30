using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Paging;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Security;

namespace Platform.Modules.Identity.Services;

/// <summary>用户管理（管理端）。</summary>
public interface IUserAdminService
{
    /// <summary>分页查询用户。</summary>
    Task<PageResult<UserSummaryDto>> ListAsync(
        PageQuery query,
        bool? isEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>用户详情。</summary>
    Task<UserDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建用户。</summary>
    Task<UserDetailDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>更新显示名/启用状态/角色集合。</summary>
    Task<UserDetailDto> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        Guid? actorId,
        CancellationToken cancellationToken = default);

    /// <summary>删除用户（内置 admin 与本人不可删除）。</summary>
    Task DeleteAsync(Guid id, Guid? actorId, CancellationToken cancellationToken = default);

    /// <summary>管理员重置口令并吊销全部会话。</summary>
    Task<int> ResetPasswordAsync(
        Guid id,
        ResetUserPasswordRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>用户管理实现。</summary>
public sealed partial class UserAdminService : IUserAdminService
{
    private readonly PlatformDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public UserAdminService(
        PlatformDbContext db,
        IPasswordHasher hasher,
        IRefreshTokenService refreshTokens,
        IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _refreshTokens = refreshTokens;
        _clock = clock;
    }

    [GeneratedRegex(IdentityConventions.UserNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex UserNameRegex();

    /// <inheritdoc />
    public async Task<PageResult<UserSummaryDto>> ListAsync(
        PageQuery query,
        bool? isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<User>()
            .AsNoTracking()
            .Include(static x => x.UserRoles)
            .ThenInclude(static x => x.Role)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim().ToLowerInvariant();
            source = source.Where(x => x.UserName.Contains(keyword) || (x.DisplayName != null && x.DisplayName.Contains(keyword)));
        }

        if (isEnabled is { } enabled)
        {
            source = source.Where(x => x.IsEnabled == enabled);
        }

        source = source.OrderBy(static x => x.UserName);

        var page = await source.ToPageResultAsync(query, cancellationToken);
        return PageResult<UserSummaryDto>.Create(
            [.. page.Items.Select(ToSummary)],
            page.Total,
            query);
    }

    /// <inheritdoc />
    public async Task<UserDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await LoadAsync(id, cancellationToken);
        return ToDetail(user);
    }

    /// <inheritdoc />
    public async Task<UserDetailDto> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userName = NormalizeUserName(request.UserName);
        PasswordPolicy.EnsureValid(request.Password);
        var displayName = NormalizeDisplayName(request.DisplayName);

        if (await _db.Set<User>().AnyAsync(x => x.UserName == userName, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.Conflict, $"用户名 {userName} 已存在。");
        }

        var roles = await LoadRolesAsync(request.RoleIds, cancellationToken);
        var now = _clock.UtcNow;
        var user = new User
        {
            UserName = userName,
            DisplayName = displayName,
            PasswordHash = _hasher.Hash(request.Password).Encoded,
            IsEnabled = request.IsEnabled,
            MustChangePassword = request.MustChangePassword,
        };

        foreach (var role in roles)
        {
            user.UserRoles.Add(new UserRole { Role = role, RoleId = role.Id, CreatedAt = now });
        }

        _db.Set<User>().Add(user);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(ApiErrorCode.Conflict, $"用户名 {userName} 已存在。", exception);
        }

        return ToDetail(user);
    }

    /// <inheritdoc />
    public async Task<UserDetailDto> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        Guid? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await LoadAsync(id, cancellationToken);
        var now = _clock.UtcNow;

        if (request.DisplayName is not null)
        {
            user.DisplayName = NormalizeDisplayName(request.DisplayName);
        }

        if (request.IsEnabled is { } enabled)
        {
            if (!enabled && actorId == id)
            {
                throw new BusinessException(ApiErrorCode.IdentityPermissionDenied, "不能禁用当前登录账号。");
            }

            user.IsEnabled = enabled;
        }

        if (request.RoleIds is not null)
        {
            var roles = await LoadRolesAsync(request.RoleIds, cancellationToken);
            user.UserRoles.Clear();
            foreach (var role in roles)
            {
                user.UserRoles.Add(new UserRole { Role = role, RoleId = role.Id, CreatedAt = now });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToDetail(user);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, Guid? actorId, CancellationToken cancellationToken = default)
    {
        var user = await LoadAsync(id, cancellationToken);

        if (string.Equals(user.UserName, IdentityConventions.AdminUserName, StringComparison.Ordinal))
        {
            throw new BusinessException(ApiErrorCode.Conflict, "内置 admin 账号不可删除。");
        }

        if (actorId == id)
        {
            throw new BusinessException(ApiErrorCode.IdentityPermissionDenied, "不能删除当前登录账号。");
        }

        _db.Set<User>().Remove(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ResetPasswordAsync(
        Guid id,
        ResetUserPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PasswordPolicy.EnsureValid(request.NewPassword);

        var user = await LoadAsync(id, cancellationToken);
        user.PasswordHash = _hasher.Hash(request.NewPassword).Encoded;
        user.MustChangePassword = request.RequireChange;
        await _db.SaveChangesAsync(cancellationToken);

        return await _refreshTokens.RevokeAllAsync(
            user.Id,
            RefreshTokenRevokeReasons.PasswordChanged,
            cancellationToken);
    }

    private async Task<User> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Set<User>()
            .Include(static x => x.UserRoles)
            .ThenInclude(static x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw BusinessException.NotFound(ApiErrorCode.IdentityUserNotFound, $"用户 {id} 不存在。");

    private async Task<IReadOnlyList<Role>> LoadRolesAsync(
        IReadOnlyList<Guid>? roleIds,
        CancellationToken cancellationToken)
    {
        if (roleIds is null || roleIds.Count == 0)
        {
            return [];
        }

        var distinct = roleIds.Distinct().ToList();
        var roles = await _db.Set<Role>()
            .Where(x => distinct.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var missing = distinct.Except(roles.Select(static x => x.Id)).ToList();
        if (missing.Count > 0)
        {
            throw BusinessException.NotFound(
                ApiErrorCode.IdentityRoleNotFound,
                $"角色不存在：{string.Join(", ", missing)}。");
        }

        return roles;
    }

    private static string NormalizeUserName(string? userName)
    {
        var normalized = userName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!UserNameRegex().IsMatch(normalized))
        {
            throw BusinessException.Invalid(
                "用户名必须为 3–64 位小写字母、数字、点、下划线或连字符，且以字母/数字开头。");
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
            throw BusinessException.Invalid("显示名不得超过 256 个字符。");
        }

        return trimmed;
    }

    private static UserSummaryDto ToSummary(User user) =>
        new(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.IsEnabled,
            user.MustChangePassword,
            ToRoleRefs(user),
            user.LastLoginAt,
            user.CreatedAt);

    private static UserDetailDto ToDetail(User user) =>
        new(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.IsEnabled,
            user.MustChangePassword,
            [.. user.UserRoles.Select(static x => x.RoleId)],
            ToRoleRefs(user),
            user.LastLoginAt,
            user.LoginCount,
            user.CreatedAt,
            user.UpdatedAt);

    private static IReadOnlyList<RoleRefDto> ToRoleRefs(User user) =>
        [.. user.UserRoles
            .Select(static x => x.Role)
            .Where(static x => x is not null)
            .Select(static x => new RoleRefDto(x!.Id, x.Name, x.DisplayName))
            .OrderBy(static x => x.Name)];
}
