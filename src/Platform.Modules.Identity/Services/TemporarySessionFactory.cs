using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Identity.Services;

/// <summary>临时会话签发结果（访问令牌 + 主体自述）。</summary>
public sealed record TemporarySession(string AccessToken, DateTimeOffset ExpiresAt, UserSessionDto User);

/// <summary>
/// 单次密钥与扫码登录共用的临时令牌：主体是签发密钥 / 批准扫码的真实用户，
/// 角色与权限取该用户当前的会话快照。令牌仍标记为临时，因此不能再签发密钥或审批扫码。
/// </summary>
public interface ITemporarySessionFactory
{
    /// <summary>按指定用户的角色与权限签发临时访问令牌。</summary>
    Task<TemporarySession> IssueAsync(
        Guid userId,
        TimeSpan lifetime,
        string? machineCode,
        CancellationToken cancellationToken = default);

    /// <summary>只构建主体自述（扫码轮询领取已签发令牌时使用）。</summary>
    Task<UserSessionDto> BuildUserAsync(Guid userId, CancellationToken cancellationToken = default);
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
        TimeSpan lifetime,
        string? machineCode,
        CancellationToken cancellationToken = default)
    {
        UserSessionContext context = await _sessions
            .BuildAsync(userId, requestedAppKey: null, cancellationToken: cancellationToken);
        UserSessionDto user = context.Session;

        var issued = _issuer.Issue(new AccessTokenSpec
        {
            Subject = user.UserId.ToString(),
            Name = user.UserName,
            Roles = user.Roles,
            Permissions = user.Permissions,
            AppKeys = user.AppKeys,
            AppKey = context.AppKey ?? (user.AppKeys.Count > 0 ? user.AppKeys[0] : null),
            Temporary = true,
            PrincipalKind = PlatformPrincipalKinds.User,
            MachineCode = machineCode,
            Lifetime = lifetime,
        });

        return new TemporarySession(issued.AccessToken, issued.ExpiresAt, user);
    }

    /// <inheritdoc />
    public async Task<UserSessionDto> BuildUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await _sessions.BuildAsync(userId, requestedAppKey: null, cancellationToken: cancellationToken)).Session;
}
