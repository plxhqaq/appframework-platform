using Platform.Kernel.Abstractions;

namespace Platform.Modules.Licensing.Domain;

/// <summary>授权产品：同一应用下按产品分别签发授权，产品键是渠道之上的授权分组轴。</summary>
public sealed class LicenseProduct : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键（隔离轴，来自令牌而不是请求参数）。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>产品键（应用内唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用；停用后不再签发与校验。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>席位上限；为空表示不限。</summary>
    public int? SeatLimit { get; set; }

    /// <summary>新签发授权的默认宽限天数（只对在线授权生效）。</summary>
    public int DefaultGraceDays { get; set; }

    /// <summary>试用天数；0 表示不提供自动试用。</summary>
    public int TrialDays { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该产品下的授权。</summary>
    public List<LicenseEntitlement> Entitlements { get; set; } = [];

    /// <summary>该产品下签发的离线码台账。</summary>
    public List<OfflineLicenseCode> OfflineCodes { get; set; } = [];
}

/// <summary>
/// 授权主体：以机器码为身份。保留 <see cref="ExpiresAt"/> 冗余列（到期 + 宽限），
/// 让"有效/过期"过滤成为普通比较，避免各数据库对日期加法的翻译差异。
/// </summary>
public sealed class LicenseEntitlement : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属产品。</summary>
    public Guid ProductId { get; set; }

    /// <summary>所属产品导航。</summary>
    public LicenseProduct Product { get; set; } = null!;

    /// <summary>机器码（16 字符 Crockford Base32；产品内唯一）。</summary>
    public string MachineCode { get; set; } = string.Empty;

    /// <summary>授权类型：trial / official。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>生效时间。</summary>
    public DateTimeOffset NotBefore { get; set; }

    /// <summary>到期时间（不含宽限）。</summary>
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>到期 + 宽限；有效状态与查询过滤都以它为准。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>宽限天数（只对在线授权生效）。</summary>
    public int GraceDays { get; set; }

    /// <summary>授权编号（人读，用于现场报障）。</summary>
    public string? LicenseNo { get; set; }

    /// <summary>备注。</summary>
    public string? Notes { get; set; }

    /// <summary>签发人（用户名或 API Key 主体）。</summary>
    public string? IssuedBy { get; set; }

    /// <summary>吊销时间；非空即永久吊销。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>吊销原因。</summary>
    public string? RevokedReason { get; set; }

    /// <summary>最近一次客户端校验时间。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>离线授权码台账。码本身自包含（签名 + 机器码 + 产品键），台账只用于签发追溯。</summary>
public sealed class OfflineLicenseCode : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属产品。</summary>
    public Guid ProductId { get; set; }

    /// <summary>所属产品导航。</summary>
    public LicenseProduct Product { get; set; } = null!;

    /// <summary>机器码（码只能在这台机器上使用）。</summary>
    public string MachineCode { get; set; } = string.Empty;

    /// <summary>授权编号。</summary>
    public string LicenseNo { get; set; } = string.Empty;

    /// <summary>离线码原文（含签名与校验位）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>授权类型：trial / official。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>签发时间。</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>到期时间。</summary>
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>宽限天数（离线码恒为 0）。</summary>
    public int GraceDays { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>签发人。</summary>
    public string? IssuedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 签名密钥登记：只存公钥与状态，私钥永远只在部署配置/环境变量里。
/// 轮换时新密钥成为 active、旧密钥置为 retired（继续参与验签），客户端通过密钥发现接口拿到全部公钥。
/// </summary>
public sealed class LicenseSigningKey : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>密钥 id（由公钥指纹派生，客户端用它选验签公钥）。</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>算法标识（当前固定 ECDSA-P256-SHA256）。</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>公钥（SPKI Base64；公钥不是秘密）。</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>公钥指纹（SPKI 的 SHA-256，小写十六进制）。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>状态：active / retired。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>启用时间。</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>退役时间（退役后仍参与验签）。</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
