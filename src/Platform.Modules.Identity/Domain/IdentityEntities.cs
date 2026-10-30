using Platform.Kernel.Abstractions;

namespace Platform.Modules.Identity.Domain;

/// <summary>
/// 平台用户。用户名统一保存为小写，登录时同样归一化后比对，
/// 避免大小写差异造成"同名不同人"。
/// </summary>
public sealed class User : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>用户名（小写，唯一）。</summary>
    public required string UserName { get; set; }

    /// <summary>显示名。</summary>
    public string? DisplayName { get; set; }

    /// <summary>Argon2id 编码串（参数随哈希存储，形如 <c>$argon2id$v=19$m=...$salt$hash</c>）。</summary>
    public required string PasswordHash { get; set; }

    /// <summary>是否启用；禁用后登录与刷新都拒绝。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否强制下次登录修改口令。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>最后登录时间。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>累计登录次数。</summary>
    public int LoginCount { get; set; }

    /// <summary>角色关联。</summary>
    public ICollection<UserRole> UserRoles { get; set; } = [];

    /// <summary>刷新令牌。</summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 角色。<see cref="Scope"/> 为 <c>platform</c> 时用于管理端；
/// 为 <c>app</c> 时绑定一个应用键，只在该应用上下文授予权限。
/// </summary>
public sealed class Role : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>角色名（小写 slug，唯一）。</summary>
    public required string Name { get; set; }

    /// <summary>显示名。</summary>
    public string? DisplayName { get; set; }

    /// <summary>作用域：<c>platform</c> / <c>app</c>。</summary>
    public string Scope { get; set; } = "platform";

    /// <summary>应用级角色绑定的应用键；平台级为空。</summary>
    public string? AppKey { get; set; }

    /// <summary>授予的权限码（可含通配符 <c>*</c>，仅限平台管理员角色）。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>内置角色：不可改名、不可删除，但允许调整权限（平台管理员除外）。</summary>
    public bool IsSystem { get; set; }

    /// <summary>用户关联。</summary>
    public ICollection<UserRole> UserRoles { get; set; } = [];

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>用户-角色关联（多对多）。</summary>
public sealed class UserRole
{
    /// <summary>用户主键。</summary>
    public Guid UserId { get; set; }

    /// <summary>用户导航。</summary>
    public User? User { get; set; }

    /// <summary>角色主键。</summary>
    public Guid RoleId { get; set; }

    /// <summary>角色导航。</summary>
    public Role? Role { get; set; }

    /// <summary>授予时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 刷新令牌。库里只存 SHA-256 哈希；同一登录会话共享 <see cref="FamilyId"/>，
/// 旧令牌被轮换后若再次出现即视为泄露，整个家族立即吊销。
/// </summary>
public sealed class RefreshToken : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属用户。</summary>
    public Guid UserId { get; set; }

    /// <summary>用户导航。</summary>
    public User? User { get; set; }

    /// <summary>会话家族 id（一次登录一个家族，轮换不改变）。</summary>
    public Guid FamilyId { get; set; }

    /// <summary>明文令牌的 SHA-256（小写十六进制）。</summary>
    public required string TokenHash { get; set; }

    /// <summary>设备指纹（客户端上报，用于按设备吊销）。</summary>
    public string? DeviceFingerprint { get; set; }

    /// <summary>签发时固定的应用上下文（刷新时沿用，避免刷新后越权切应用）。</summary>
    public string? AppKey { get; set; }

    /// <summary>绑定的机器码（可空）。</summary>
    public string? MachineCode { get; set; }

    /// <summary>签发时的客户端 IP。</summary>
    public string? CreatedByIp { get; set; }

    /// <summary>过期时间。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>吊销时间；非空即失效。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>吊销原因：<c>rotated</c> / <c>logout</c> / <c>reuse_detected</c> / <c>revoked_by_admin</c> / <c>password_changed</c>。</summary>
    public string? RevokedReason { get; set; }

    /// <summary>轮换后的新令牌 id。</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>最后使用时间。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>刷新令牌吊销原因。</summary>
public static class RefreshTokenRevokeReasons
{
    /// <summary>正常轮换。</summary>
    public const string Rotated = "rotated";

    /// <summary>主动登出。</summary>
    public const string Logout = "logout";

    /// <summary>检测到已轮换令牌被复用（按泄露处理，吊销整个家族）。</summary>
    public const string ReuseDetected = "reuse_detected";

    /// <summary>管理员吊销。</summary>
    public const string RevokedByAdmin = "revoked_by_admin";

    /// <summary>修改口令后吊销。</summary>
    public const string PasswordChanged = "password_changed";
}

/// <summary>
/// 登录失败节流。按账号与 IP 两个维度落库：内存计数在多副本部署时失效，
/// 而这两个维度分别拦截"分布式撞库同一账号"与"同一 IP 慢速撞多个账号"。
/// </summary>
public sealed class LoginThrottle : IAuditableEntity
{
    /// <summary>账号维度。</summary>
    public const string DimensionAccount = "account";

    /// <summary>IP 维度。</summary>
    public const string DimensionIp = "ip";

    /// <summary>维度（<c>account</c> / <c>ip</c>）。</summary>
    public required string Dimension { get; set; }

    /// <summary>维度键（归一化用户名或 IP）。</summary>
    public required string Key { get; set; }

    /// <summary>窗口内失败次数。</summary>
    public int FailureCount { get; set; }

    /// <summary>计数窗口起点。</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>锁定截止时间；为空表示未锁定。</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
