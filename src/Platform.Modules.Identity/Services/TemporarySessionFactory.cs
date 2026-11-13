using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Services;

/// <summary>临时会话签发结果（访问令牌 + 主体自述）。</summary>
public sealed record TemporarySession(string AccessToken, DateTimeOffset ExpiresAt, UserSessionDto User);

/// <summary>
/// 单次密钥与扫码登录共用的临时身份：不是真实用户，不落用户表，
/// 权限取自内置 <c>engineer</c> 角色（去掉签发密钥/审批扫码），因此不引入"影子账号"这份第二事实源。
/// </summary>
public interface ITemporarySessionFactory
{
    /// <summary>按内置工程师角色签发临时访问令牌。</summary>
    Task<TemporarySession> IssueAsync(
        Guid subjectId,
        TimeSpan lifetime,
        string? machineCode,
        CancellationToken cancellationToken = default);

    /// <summary>只构建主体自述（扫码轮询领取已签发令牌时使用）。</summary>
    Task<UserSessionDto> BuildUserAsync(Guid subjectId, CancellationToken cancellationToken = default);
}

/// <summary>临时会话工厂实现。</summary>
public sealed class TemporarySessionFactory : ITemporarySessionFactory
{
    private readonly PlatformDbContext _db;
    private readonly IAppDirectory _apps;
    private readonly IAccessTokenIssuer _issuer;

    /// <summary>构造。</summary>
    public TemporarySessionFactory(PlatformDbContext db, IAppDirectory apps, IAccessTokenIssuer issuer)
    {
        _db = db;
        _apps = apps;
        _issuer = issuer;
    }

    /// <inheritdoc />
    public async Task<TemporarySession> IssueAsync(
        Guid subjectId,
        TimeSpan lifetime,
        string? machineCode,
        CancellationToken cancellationToken = default)
    {
        var user = await BuildUserAsync(subjectId, cancellationToken);
        var issued = _issuer.Issue(new AccessTokenSpec
        {
            Subject = subjectId.ToString(),
            Name = user.UserName,
            Roles = user.Roles,
            Permissions = user.Permissions,
            AppKeys = user.AppKeys,
            AppKey = user.AppKeys.Count > 0 ? user.AppKeys[0] : null,
            Temporary = true,
            PrincipalKind = PlatformPrincipalKinds.User,
            MachineCode = machineCode,
            Lifetime = lifetime,
        });

        return new TemporarySession(issued.AccessToken, issued.ExpiresAt, user);
    }

    /// <inheritdoc />
    public async Task<UserSessionDto> BuildUserAsync(Guid subjectId, CancellationToken cancellationToken = default)
    {
        var role = await _db.Set<Role>()
                       .AsNoTracking()
                       .FirstOrDefaultAsync(x => x.Name == SingleLoginConventions.EngineerRole, cancellationToken)
                   ?? throw new BusinessException(
                       ApiErrorCode.IdentityPermissionDenied,
                       "内置工程师角色缺失，无法签发临时会话。请检查身份种子。");

        var permissions = role.Permissions
            .Where(PermissionCatalog.IsKnown)
            // 工程师角色本身带签发/审批入口（真人账号要用），临时会话必须摘掉，否则就能给自己续期。
            .Where(static code => code is not (PermissionCatalog.IdentitySingleKeyIssue or PermissionCatalog.IdentityQrApprove))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var appKeys = await _apps.ListEnabledKeysAsync(cancellationToken);

        return new UserSessionDto(
            subjectId,
            SingleLoginConventions.TemporaryUserName,
            SingleLoginConventions.TemporaryDisplayName,
            [role.Name],
            permissions,
            appKeys,
            MustChangePassword: false);
    }
}
