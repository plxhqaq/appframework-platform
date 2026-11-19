using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Security;

namespace Platform.Modules.Identity.Services;

/// <summary>一次完整会话：访问令牌 + 刷新令牌 + 主体信息。</summary>
public sealed record IssuedSession(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    UserSessionDto User);

/// <summary>换发结果：只换访问令牌，刷新令牌保持不变。</summary>
public sealed record SwitchedSession(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    UserSessionDto User);

/// <summary>用户名口令登录、刷新、登出与自助改密。</summary>
public interface IIdentityAuthService
{
    /// <summary>桌面端登录（刷新令牌放在响应体）。</summary>
    Task<LoginResponse> LoginAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>管理端登录（刷新令牌由调用方写入 httpOnly Cookie）。</summary>
    Task<IssuedSession> AdminLoginAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>刷新并轮换令牌。</summary>
    Task<IssuedSession> RefreshAsync(
        string? refreshToken,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换当前会话的应用上下文并换发访问令牌。
    /// 刷新令牌记录同步记下应用键，后续静默续期保持同一上下文。
    /// </summary>
    Task<SwitchedSession> SwitchAppContextAsync(
        Guid userId,
        SwitchAppContextRequest request,
        string? refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>登出（幂等）。</summary>
    Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default);

    /// <summary>自助修改口令；成功后吊销全部刷新令牌。</summary>
    Task<PasswordChangedResponse> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>认证服务实现。</summary>
public sealed class IdentityAuthService : IIdentityAuthService
{
    private const string InvalidCredentialsMessage = "用户名或口令错误。";

    private readonly PlatformDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ILoginThrottleService _throttle;
    private readonly IUserSessionFactory _sessions;
    private readonly IAccessTokenIssuer _issuer;
    private readonly ISessionRevocationNotifier _push;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public IdentityAuthService(
        PlatformDbContext db,
        IPasswordHasher hasher,
        IRefreshTokenService refreshTokens,
        ILoginThrottleService throttle,
        IUserSessionFactory sessions,
        IAccessTokenIssuer issuer,
        ISessionRevocationNotifier push,
        IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _refreshTokens = refreshTokens;
        _throttle = throttle;
        _sessions = sessions;
        _issuer = issuer;
        _push = push;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var issued = await AuthenticateAsync(request, ipAddress, cancellationToken);

        return new LoginResponse(
            issued.AccessToken,
            issued.ExpiresAt,
            issued.RefreshToken,
            issued.RefreshExpiresAt,
            issued.User);
    }

    /// <inheritdoc />
    public Task<IssuedSession> AdminLoginAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        AuthenticateAsync(request, ipAddress, cancellationToken);

    /// <inheritdoc />
    public async Task<IssuedSession> RefreshAsync(
        string? refreshToken,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityRefreshTokenInvalid,
                "缺少刷新令牌，请重新登录。");
        }

        var rotated = await _refreshTokens.RotateAsync(refreshToken, ipAddress, cancellationToken);
        var context = await _sessions.BuildAsync(rotated.UserId, rotated.AppKey, cancellationToken);
        var access = IssueAccessToken(context, rotated.MachineCode, rotated.InstallGuid);

        return new IssuedSession(
            access.AccessToken,
            access.ExpiresAt,
            rotated.Plaintext,
            rotated.ExpiresAt,
            context.Session);
    }

    /// <inheritdoc />
    public async Task<SwitchedSession> SwitchAppContextAsync(
        Guid userId,
        SwitchAppContextRequest request,
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var appKey = request.AppKey?.Trim();
        if (string.IsNullOrEmpty(appKey))
        {
            throw BusinessException.Invalid("必须指定要进入的应用键。");
        }

        // BuildAsync 校验用户是否有权进入该应用（109），先校验再落库，避免无效切换改坏刷新令牌。
        var context = await _sessions.BuildAsync(userId, appKey, cancellationToken);

        string? machineCode = null;
        Guid? installGuid = null;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var stored = await _refreshTokens.SetAppKeyAsync(
                refreshToken, userId, context.AppKey, cancellationToken);
            machineCode = stored?.MachineCode;
            installGuid = stored?.InstallGuid;
        }

        var access = IssueAccessToken(context, machineCode, installGuid);
        return new SwitchedSession(access.AccessToken, access.ExpiresAt, context.Session);
    }

    /// <inheritdoc />
    public Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default) =>
        // 令牌不存在也返回成功：登出必须幂等，客户端不应因重复点击而报错。
        _refreshTokens.RevokeAsync(
            refreshToken,
            RefreshTokenRevokeReasons.Logout,
            cancellationToken);

    /// <inheritdoc />
    public async Task<PasswordChangedResponse> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _db.Set<User>().FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
                   ?? throw BusinessException.NotFound(ApiErrorCode.IdentityUserNotFound, $"用户 {userId} 不存在。");

        if (!_hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            throw new BusinessException(ApiErrorCode.IdentityInvalidCredentials, "当前口令不正确。");
        }

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPasswordPolicyViolation,
                "新口令不能与当前口令相同。");
        }

        PasswordPolicy.EnsureValid(request.NewPassword);
        user.PasswordHash = _hasher.Hash(request.NewPassword).Encoded;
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(cancellationToken);

        // 吊销会清掉活跃行，先抓设备归属再推 UserSessionRevoked。
        var targets = await _push.CaptureUserTargetsAsync(user.Id, cancellationToken);
        var revoked = await _refreshTokens.RevokeAllAsync(
            user.Id,
            RefreshTokenRevokeReasons.PasswordChanged,
            cancellationToken);
        if (revoked > 0)
        {
            await _push.NotifyAsync(user.Id, RefreshTokenRevokeReasons.PasswordChanged, targets, userWide: true, cancellationToken);
        }

        return new PasswordChangedResponse(_clock.UtcNow, revoked);
    }

    private async Task<IssuedSession> AuthenticateAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userName = request.UserName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (userName.Length == 0 || string.IsNullOrEmpty(request.Password))
        {
            throw new BusinessException(ApiErrorCode.IdentityInvalidCredentials, InvalidCredentialsMessage);
        }

        await _throttle.EnsureNotLockedAsync(userName, ipAddress, cancellationToken);

        var user = await _db.Set<User>()
            .FirstOrDefaultAsync(x => x.UserName == userName, cancellationToken);

        if (user is null || !_hasher.Verify(request.Password, user.PasswordHash))
        {
            await _throttle.RegisterFailureAsync(userName, ipAddress, cancellationToken);
            throw new BusinessException(ApiErrorCode.IdentityInvalidCredentials, InvalidCredentialsMessage);
        }

        if (!user.IsEnabled)
        {
            throw new BusinessException(ApiErrorCode.IdentityUserDisabled, $"用户 {user.UserName} 已被禁用。");
        }

        await _throttle.RegisterSuccessAsync(userName, cancellationToken);

        // 参数调整后旧哈希仍能校验；登录成功顺手升级到当前参数。
        if (_hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = _hasher.Hash(request.Password).Encoded;
        }

        user.LastLoginAt = _clock.UtcNow;
        user.LoginCount++;
        await _db.SaveChangesAsync(cancellationToken);

        string? machineCode;
        try
        {
            machineCode = DeviceIdentityHasher.NormalizeMachineCode(request.MachineCode);
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Invalid(exception.Message);
        }

        var context = await _sessions.BuildAsync(user.Id, request.AppKey, cancellationToken);
        var refresh = await _refreshTokens.IssueAsync(
            user.Id,
            context.AppKey,
            machineCode,
            request.InstallGuid,
            request.DeviceFingerprint,
            ipAddress,
            cancellationToken);
        var access = IssueAccessToken(context, machineCode, request.InstallGuid);

        return new IssuedSession(
            access.AccessToken,
            access.ExpiresAt,
            refresh.Plaintext,
            refresh.ExpiresAt,
            context.Session);
    }

    private IssuedToken IssueAccessToken(
        UserSessionContext context,
        string? machineCode,
        Guid? installGuid = null) =>
        _issuer.Issue(new AccessTokenSpec
        {
            Subject = context.Session.UserId.ToString(),
            Name = context.Session.UserName,
            Roles = context.Session.Roles,
            Permissions = context.Session.Permissions,
            AppKeys = context.Session.AppKeys,
            AppKey = context.AppKey,
            PrincipalKind = PlatformPrincipalKinds.User,
            MachineCode = machineCode,
            InstallGuid = installGuid,
            TokenEpoch = context.TokenEpoch,
        });
}
