using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Identity.Services;

/// <summary>临时会话签发结果（访问令牌 + 主体自述）。</summary>
public sealed record TemporarySession(string AccessToken, DateTimeOffset ExpiresAt, UserSessionDto User);

/// <summary>
/// 单次密钥与扫码登录共用的临时令牌：主体是签发密钥 / 批准扫码的真实用户，
/// 角色与权限取该用户在当前应用下的会话快照。令牌仍标记为临时，因此不能再签发密钥或审批扫码。
/// </summary>
public interface ITemporarySessionFactory
{
    /// <summary>按指定用户、目标应用、机器与安装实例签发临时访问令牌。</summary>
    Task<TemporarySession> IssueAsync(
        Guid userId,
        string appKey,
        TimeSpan lifetime,
        string? machineCode,
        Guid? installGuid,
        CancellationToken cancellationToken = default);

    /// <summary>只构建主体自述（扫码轮询领取已签发令牌时使用）。</summary>
    Task<UserSessionDto> BuildUserAsync(
        Guid userId,
        string appKey,
        CancellationToken cancellationToken = default);
}

/// <summary>临时会话工厂实现。</summary>
public sealed class TemporarySessionFactory : ITemporarySessionFactory
{
    private readonly IUserSessionFactory _sessions;
    private readonly IAccessTokenIssuer _issuer;

    /// <summary>构造。</summary>
    public TemporarySessionFactory(IUserSessionFactory sessions, IAccessTokenIssuer issuer)
    {
        _sessions = sessions;
        _issuer = issuer;
    }

    /// <inheritdoc />
    public async Task<TemporarySession> IssueAsync(
        Guid userId,
        string appKey,
        TimeSpan lifetime,
        string? machineCode,
        Guid? installGuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        // BuildAsync 校验用户可访问该应用（无权即 403）；不再回落到用户的第一个应用。
        UserSessionContext context = await _sessions
            .BuildAsync(userId, appKey, cancellationToken: cancellationToken);
        UserSessionDto user = context.Session;

        var issued = _issuer.Issue(new AccessTokenSpec
        {
            Subject = user.UserId.ToString(),
            Name = user.UserName,
            Roles = user.Roles,
            Permissions = user.Permissions,
            AppKeys = user.AppKeys,
            AppKey = context.AppKey ?? appKey,
            Temporary = true,
            PrincipalKind = PlatformPrincipalKinds.User,
            MachineCode = machineCode,
            InstallGuid = installGuid,
            Lifetime = lifetime,
        });

        return new TemporarySession(issued.AccessToken, issued.ExpiresAt, user);
    }

    /// <inheritdoc />
    public async Task<UserSessionDto> BuildUserAsync(
        Guid userId,
        string appKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        return (await _sessions.BuildAsync(userId, appKey, cancellationToken)).Session;
    }
}
