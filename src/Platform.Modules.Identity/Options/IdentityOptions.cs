using Platform.Contracts.Identity;

namespace Platform.Modules.Identity.Options;

/// <summary>登录失败节流配置（<c>LoginThrottle</c> 节点）。账号与 IP 两个维度共用同一组阈值。</summary>
public sealed class LoginThrottleOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "LoginThrottle";

    /// <summary>窗口内失败达到此数即锁定。</summary>
    public int MaxFailuresBeforeLockout { get; set; } = 10;

    /// <summary>锁定时长（分钟）。锁定期间即使口令正确也拒绝，避免接口变成口令 oracle。</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>失败计数窗口（分钟）；超过窗口的旧失败不再累计。</summary>
    public int TrackingWindowMinutes { get; set; } = 15;
}

/// <summary>刷新令牌配置，复用宿主 <c>Jwt</c> 节的 <c>RefreshTokenDays</c>。</summary>
public sealed class IdentityTokenOptions
{
    /// <summary>配置节名（与宿主 JwtOptions 同一节，只读取刷新令牌相关字段）。</summary>
    public const string SectionName = "Jwt";

    /// <summary>刷新令牌有效期（天）。</summary>
    public int RefreshTokenDays { get; set; } = 30;
}

/// <summary>单次密钥配置（<c>SingleLogin</c> 节点）。</summary>
public sealed class SingleLoginOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "SingleLogin";

    /// <summary>默认有效期（分钟）。</summary>
    public int DefaultDurationMinutes { get; set; } = SingleLoginConventions.DefaultDurationMinutes;

    /// <summary>有效期上限（分钟，默认 2 小时）；签发时超过即钳制。</summary>
    public int MaxDurationMinutes { get; set; } = SingleLoginConventions.MaxDurationMinutes;

    /// <summary>最大兑换尝试次数；达到后密钥作废，抑制按键枚举。</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>扫码登录配置（<c>QrLogin</c> 节点）。</summary>
public sealed class QrLoginOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "QrLogin";

    /// <summary>待审批会话存活时间（分钟）。</summary>
    public int ExpireMinutes { get; set; } = QrLoginConventions.ExpireMinutes;

    /// <summary>审批通过后临时令牌的默认有效期（分钟）。</summary>
    public int DefaultDurationMinutes { get; set; } = QrLoginConventions.DefaultDurationMinutes;

    /// <summary>审批时可指定的有效期上限（分钟，默认 2 小时）。</summary>
    public int MaxDurationMinutes { get; set; } = QrLoginConventions.MaxDurationMinutes;

    /// <summary>
    /// 二维码里写进链接的管理台公网基址，例如 <c>https://ops.example.com:8444</c>。
    /// </summary>
    /// <remarks>
    /// 必须显式配置：反向代理转发时 <c>Host</c> 头常被改写成不带端口的主机名，
    /// 从请求里推出来的地址手机扫开会连不上。留空才退回按当前请求推导（仅供本机开发）。
    /// </remarks>
    public string? ApprovalUrlBase { get; set; }
}

/// <summary>身份过期数据清理配置（<c>Identity:Cleanup</c> 节点）。</summary>
/// <remarks>
/// 保留期以「失效时间」为起点：过期/吊销后再留一段时间，便于排查复用与审计；
/// 登录节流还叠加计数窗口，避免删掉仍在累计的失败记录。
/// </remarks>
public sealed class IdentityCleanupOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Identity:Cleanup";

    /// <summary>单批删除行数，避免一次删太多拖长事务。</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>刷新令牌过期后的保留天数。</summary>
    public int RefreshTokenRetentionDays { get; set; } = 7;

    /// <summary>单次密钥过期后的保留小时数（含已兑换的密钥）。</summary>
    public int SingleKeyRetentionHours { get; set; } = 24;

    /// <summary>扫码会话过期后的保留小时数。</summary>
    public int QrSessionRetentionHours { get; set; } = 24;

    /// <summary>登录节流记录在计数窗口结束后的保留小时数。</summary>
    public int LoginThrottleRetentionHours { get; set; } = 24;
}

/// <summary>幂等种子配置（<c>Seed</c> 节点）。管理员初始口令只从环境变量注入，仓库里只放开发默认值。</summary>
public sealed class IdentitySeedOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Seed";

    /// <summary>是否启用种子；测试默认关闭。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>管理员用户名。</summary>
    public string AdminUserName { get; set; } = "admin";

    /// <summary>管理员初始口令；为空则跳过管理员创建（角色与应用仍会创建）。</summary>
    public string? AdminPassword { get; set; }

    /// <summary>管理员显示名。</summary>
    public string AdminDisplayName { get; set; } = "平台管理员";

    /// <summary>演示应用键。</summary>
    public string DemoAppKey { get; set; } = "demo";

    /// <summary>演示应用名称。</summary>
    public string DemoAppName { get; set; } = "演示应用";
}
