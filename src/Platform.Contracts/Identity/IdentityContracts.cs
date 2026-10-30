namespace Platform.Contracts.Identity;

/// <summary>身份约定常量。口令策略、令牌格式与种子名称集中在这里，服务端与前端共享同一口径。</summary>
public static class IdentityConventions
{
    /// <summary>管理端刷新令牌 Cookie 名（httpOnly + SameSite=Strict）。</summary>
    public const string RefreshCookieName = "af_refresh";

    /// <summary>刷新 Cookie 的作用路径：只随管理端认证端点发送，不污染其它接口。</summary>
    public const string RefreshCookiePath = "/api/v1/admin/auth";

    /// <summary>刷新令牌明文前缀（便于日志脱敏与人工识别，不承载秘密）。</summary>
    public const string RefreshTokenPrefix = "afr_";

    /// <summary>刷新令牌随机字节数（Base64Url 编码后约 43 字符）。</summary>
    public const int RefreshTokenBytes = 32;

    /// <summary>用户名最小长度。</summary>
    public const int UserNameMinLength = 3;

    /// <summary>用户名最大长度。</summary>
    public const int UserNameMaxLength = 64;

    /// <summary>用户名正则：小写字母/数字开头，允许点、下划线、连字符。</summary>
    public const string UserNamePattern = "^[a-z0-9][a-z0-9._-]{2,63}$";

    /// <summary>口令最小长度。</summary>
    public const int PasswordMinLength = 8;

    /// <summary>口令最大长度（防止超长输入拖垮 Argon2）。</summary>
    public const int PasswordMaxLength = 128;

    /// <summary>角色名正则：小写字母开头的小写 slug。</summary>
    public const string RoleNamePattern = "^[a-z][a-z0-9-]{1,63}$";

    /// <summary>角色作用域：平台级（管理端）。</summary>
    public const string ScopePlatform = "platform";

    /// <summary>角色作用域：应用级（只在该应用上下文生效）。</summary>
    public const string ScopeApp = "app";

    /// <summary>内置管理员用户名。</summary>
    public const string AdminUserName = "admin";

    /// <summary>内置平台管理员角色。</summary>
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>内置应用管理员角色。</summary>
    public const string AppAdminRole = "app-admin";

    /// <summary>内置只读角色。</summary>
    public const string ViewerRole = "viewer";

    /// <summary>种子应用键。</summary>
    public const string DemoAppKey = "demo";
}

/// <summary>角色作用域工具。</summary>
public static class IdentityRoleScopes
{
    /// <summary>是否为合法作用域。</summary>
    public static bool IsValid(string? scope) =>
        string.Equals(scope, IdentityConventions.ScopePlatform, StringComparison.Ordinal)
        || string.Equals(scope, IdentityConventions.ScopeApp, StringComparison.Ordinal);

    /// <summary>规范化：空值默认平台级，其它非法值原样返回交给校验拒绝。</summary>
    public static string Normalize(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? IdentityConventions.ScopePlatform : scope.Trim().ToLowerInvariant();
}

/// <summary>登录请求。桌面端与管理端共用；管理端忽略 <see cref="AppKey"/> 并改用 Cookie 保存刷新令牌。</summary>
/// <param name="UserName">用户名（大小写不敏感）。</param>
/// <param name="Password">口令。</param>
/// <param name="AppKey">期望进入的应用上下文；必须在用户可访问的应用内。</param>
/// <param name="DeviceFingerprint">设备指纹（客户端生成，用于按设备吊销会话）。</param>
/// <param name="MachineCode">机器码（授权绑定用，可空）。</param>
public sealed record LoginRequest(
    string UserName,
    string Password,
    string? AppKey = null,
    string? DeviceFingerprint = null,
    string? MachineCode = null);

/// <summary>登录后的主体信息（令牌声明与响应共用同一份口径）。</summary>
/// <param name="AppKey">本次令牌固定的应用上下文；平台上下文为 null。</param>
public sealed record UserSessionDto(
    Guid UserId,
    string UserName,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> AppKeys,
    bool MustChangePassword,
    string? AppKey = null);

/// <summary>桌面端登录响应：刷新令牌放在 body（客户端自行安全存储）。</summary>
public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    UserSessionDto User);

/// <summary>管理端登录响应：刷新令牌只存在于 httpOnly Cookie，响应体不含它。</summary>
public sealed record AdminSessionResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    UserSessionDto User);

/// <summary>刷新令牌请求（桌面端）。</summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// 切换当前会话的应用上下文：管理台进入某个应用的模块（版本、授权等）时使用。
/// 只换发访问令牌，刷新令牌记录同步记下应用键，后续静默续期仍落在同一上下文。
/// </summary>
public sealed record SwitchAppContextRequest(string? AppKey);

/// <summary>刷新结果：access 与 refresh 成对轮换，旧 refresh 立即失效。</summary>
public sealed record RefreshTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    UserSessionDto User);

/// <summary>登出请求；刷新令牌可空（幂等：没有令牌时也返回成功）。</summary>
public sealed record LogoutRequest(string? RefreshToken = null);

/// <summary>自助修改口令。</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>修改口令结果。</summary>
public sealed record PasswordChangedResponse(DateTimeOffset ChangedAt, int RevokedSessions);

/// <summary>角色引用（用户列表与表单使用）。</summary>
public sealed record RoleRefDto(Guid Id, string Name, string? DisplayName);

/// <summary>用户概要。</summary>
public sealed record UserSummaryDto(
    Guid Id,
    string UserName,
    string? DisplayName,
    bool IsEnabled,
    bool MustChangePassword,
    IReadOnlyList<RoleRefDto> Roles,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

/// <summary>用户详情。</summary>
public sealed record UserDetailDto(
    Guid Id,
    string UserName,
    string? DisplayName,
    bool IsEnabled,
    bool MustChangePassword,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<RoleRefDto> Roles,
    DateTimeOffset? LastLoginAt,
    int LoginCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>创建用户。管理员设定的初始口令对管理员已知，默认要求本人首登改密。</summary>
public sealed record CreateUserRequest(
    string UserName,
    string Password,
    string? DisplayName = null,
    bool IsEnabled = true,
    IReadOnlyList<Guid>? RoleIds = null,
    bool MustChangePassword = true);

/// <summary>更新用户；字段为空表示不修改，角色集合为空表示清空角色。</summary>
public sealed record UpdateUserRequest(
    string? DisplayName = null,
    bool? IsEnabled = null,
    IReadOnlyList<Guid>? RoleIds = null);

/// <summary>管理员重置口令。</summary>
public sealed record ResetUserPasswordRequest(string NewPassword, bool RequireChange = true);

/// <summary>角色概要。</summary>
public sealed record RoleSummaryDto(
    Guid Id,
    string Name,
    string? DisplayName,
    string Scope,
    string? AppKey,
    IReadOnlyList<string> Permissions,
    bool IsSystem,
    int UserCount,
    DateTimeOffset CreatedAt);

/// <summary>创建角色。</summary>
public sealed record CreateRoleRequest(
    string Name,
    string? DisplayName = null,
    string Scope = IdentityConventions.ScopePlatform,
    string? AppKey = null,
    IReadOnlyList<string>? Permissions = null);

/// <summary>更新角色；字段为空表示不修改。</summary>
public sealed record UpdateRoleRequest(
    string? DisplayName = null,
    string? Scope = null,
    string? AppKey = null,
    IReadOnlyList<string>? Permissions = null);

/// <summary>会话（一条 refresh token 记录）。</summary>
public sealed record SessionSummaryDto(
    Guid Id,
    Guid UserId,
    string UserName,
    Guid FamilyId,
    string? DeviceFingerprint,
    string? CreatedByIp,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool IsActive);
