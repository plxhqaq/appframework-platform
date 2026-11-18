namespace Platform.Contracts.Licensing;

/// <summary>授权模块常量与校验口径（服务端与客户端共用）。</summary>
public static class LicenseConventions
{
    /// <summary>产品键：小写字母开头，2–32 位小写字母/数字/连字符。</summary>
    public const string ProductKeyPattern = "^[a-z][a-z0-9-]{1,31}$";

    /// <summary>产品键最大字符数。</summary>
    public const int MaxProductKeyChars = 32;

    /// <summary>机器码字节数（主标识哈希）。</summary>
    public const int MachineCodeBytes = 10;

    /// <summary>机器码字符数（Crockford Base32 编码后的长度）。</summary>
    public const int MachineCodeChars = 16;

    /// <summary>授权编号最大字符数（可打印 ASCII，写进离线码的 noLen 单字节）。</summary>
    public const int MaxLicenseNoChars = 32;

    /// <summary>在线授权允许的最大宽限天数。</summary>
    public const int MaxGraceDays = 30;

    /// <summary>默认宽限天数。</summary>
    public const int DefaultGraceDays = 7;

    /// <summary>默认授权天数。</summary>
    public const int DefaultLicenseDays = 365;

    /// <summary>单次签发的最大授权天数（10 年）。</summary>
    public const int MaxLicenseDays = 3650;

    /// <summary>单个产品允许的最大席位数。</summary>
    public const int MaxSeatLimit = 100_000;

    /// <summary>试用期最大天数。</summary>
    public const int MaxTrialDays = 365;

    /// <summary>试用授权。</summary>
    public const string KindTrial = "trial";

    /// <summary>正式授权。</summary>
    public const string KindOfficial = "official";

    /// <summary>有效。</summary>
    public const string StatusActive = "active";

    /// <summary>已过期（宽限期也结束）。</summary>
    public const string StatusExpired = "expired";

    /// <summary>已吊销（不因到期而改变）。</summary>
    public const string StatusRevoked = "revoked";

    /// <summary>吊销范围：在线授权与已签发的离线授权码一起失效。</summary>
    public const string RevokeScopeAll = "all";

    /// <summary>吊销范围：只注销在线授权；已签发的离线授权码继续有效。</summary>
    public const string RevokeScopeOnline = "online";

    /// <summary>未授权（产品未给该机器签发，且未启用试用）。</summary>
    public const string StatusUnlicensed = "unlicensed";

    /// <summary>未启用授权（服务端/客户端未配置签名密钥）。</summary>
    public const string StatusUnmanaged = "unmanaged";

    /// <summary>签名密钥处于启用状态（用于签发新快照/离线码）。</summary>
    public const string SigningKeyActive = "active";

    /// <summary>签名密钥已退役（仅保留验签能力）。</summary>
    public const string SigningKeyRetired = "retired";

    /// <summary>签名算法标识。</summary>
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256";

    /// <summary>是否合法的吊销范围。</summary>
    public static bool IsValidRevokeScope(string? scope) =>
        scope is null or RevokeScopeAll or RevokeScopeOnline;

    /// <summary>是否合法的授权类型。</summary>
    public static bool IsValidKind(string? kind) =>
        kind is KindTrial or KindOfficial;

    /// <summary>是否合法的授权状态。</summary>
    public static bool IsValidStatus(string? status) =>
        status is StatusActive or StatusExpired or StatusRevoked or StatusUnlicensed or StatusUnmanaged;

    /// <summary>规范化机器码：剥离分隔符、转大写，并校验为 10 字节哈希对应的 16 字符 Crockford Base32。</summary>
    public static bool TryNormalizeMachineCode(string? machineCode, out string normalized)
    {
        normalized = string.Empty;
        string compact = Base32Crockford.Normalize(machineCode);
        if (compact.Length != MachineCodeChars
            || !Base32Crockford.TryDecode(compact, out byte[] bytes)
            || bytes.Length != MachineCodeBytes)
        {
            return false;
        }

        // 重新编码得到规范形态：手抄的 O/I/L 会被纠正，避免同一台机器存成两种机器码
        normalized = Base32Crockford.Encode(bytes);
        return true;
    }
}

/// <summary>授权产品（同一应用下按产品分别签发授权）。</summary>
public sealed record LicenseProductDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    int? SeatLimit,
    int DefaultGraceDays,
    int TrialDays,
    int EntitlementCount,
    int ActiveEntitlementCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>创建授权产品。</summary>
public sealed record CreateLicenseProductRequest(
    string? Key,
    string? Name,
    string? Description,
    int? SeatLimit = null,
    int DefaultGraceDays = LicenseConventions.DefaultGraceDays,
    int TrialDays = 0);

/// <summary>更新授权产品；字段缺省表示不修改。</summary>
public sealed record UpdateLicenseProductRequest(
    string? Name,
    string? Description,
    bool? IsEnabled,
    int? SeatLimit,
    int? DefaultGraceDays,
    int? TrialDays);

/// <summary>授权的状态由到期时间、宽限期与吊销时间共同决定。节点信息按机器码关联，不落库。</summary>
public sealed record LicenseEntitlementDto(
    Guid Id,
    string ProductKey,
    string MachineCode,
    string Kind,
    string Status,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    string? LicenseNo,
    string? Notes,
    string? IssuedBy,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RevokeScope = null,
    Guid? NodeId = null,
    string? NodeDisplayName = null);

/// <summary>签发正式授权。机器码是授权主体，跨机器复制无效；NodeId 与 MachineCode 至少给一个。</summary>
public sealed record IssueLicenseRequest(
    string? ProductKey,
    string? MachineCode,
    string? LicenseNo,
    DateTimeOffset? NotAfter,
    int? AddDays,
    int? GraceDays,
    string? Notes,
    Guid? NodeId = null);

/// <summary>续期；<see cref="NotAfter"/> 与 <see cref="AddDays"/> 二选一，后者相对当前到期时间追加。</summary>
public sealed record RenewLicenseRequest(
    DateTimeOffset? NotAfter,
    int? AddDays,
    int? GraceDays,
    string? Notes);

/// <summary>吊销授权。<see cref="Scope"/> 缺省为 <see cref="LicenseConventions.RevokeScopeAll"/>。</summary>
public sealed record RevokeLicenseRequest(string? Reason, string? Scope = null);

/// <summary>签发离线授权码（无联网现场）。NodeId 与 MachineCode 至少给一个。</summary>
public sealed record IssueOfflineLicenseCodeRequest(
    string? ProductKey,
    string? MachineCode,
    DateTimeOffset? NotAfter,
    int? AddDays,
    string? LicenseNo,
    string? Remark,
    Guid? NodeId = null);

/// <summary>离线授权码台账。<see cref="Code"/> 为原始码，<see cref="FormattedCode"/> 仅供人读。</summary>
public sealed record OfflineLicenseCodeDto(
    Guid Id,
    string ProductKey,
    string MachineCode,
    string LicenseNo,
    string Code,
    string FormattedCode,
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter,
    int GraceDays,
    string? Remark,
    string? IssuedBy,
    DateTimeOffset CreatedAt);

/// <summary>校验离线授权码（主要给客户端与服务端测试使用；离线场景由客户端本地验签）。</summary>
/// <param name="Code">授权码原文（可带分隔符）。</param>
/// <param name="MachineCode">本机机器码。</param>
/// <param name="ProductKey">当前产品键。</param>
/// <param name="AppKey">当前应用键；为空时以令牌应用上下文为准。</param>
public sealed record VerifyOfflineLicenseCodeRequest(
    string? Code,
    string? MachineCode,
    string? ProductKey,
    string? AppKey = null);

/// <summary>离线码校验结果；签名、版本、机器码、产品与应用绑定均已通过才会 <see cref="IsValid"/>。</summary>
public sealed record OfflineLicenseVerificationDto(
    bool IsValid,
    string ProductKey,
    string MachineCode,
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter,
    int GraceDays,
    string? LicenseNo,
    string? AppKey = null);

/// <summary>客户端校验在线授权。</summary>
public sealed record LicenseStatusRequest(string? ProductKey, string? MachineCode);

/// <summary>
/// 在线授权状态。<see cref="Snapshot"/> 是带签名的授权快照（Base64），
/// 客户端用 <see cref="TrustedKeys"/> 中的公钥验签后再采信；推送只作提示。
/// </summary>
public sealed record LicenseStatusResponse(
    string AppKey,
    string ProductKey,
    string MachineCode,
    string Status,
    string? Kind,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    int GraceDays,
    DateTimeOffset ServerTime,
    string? LicenseNo,
    string? KeyId,
    string? Snapshot,
    IReadOnlyList<LicenseKeyDescriptorDto> TrustedKeys,
    Guid? NodeId = null);

/// <summary>可下发客户端的签名公钥（公钥不是秘密，用于验签与轮换过渡）。</summary>
public sealed record LicenseKeyDescriptorDto(
    string KeyId,
    string Algorithm,
    string PublicKey,
    string Fingerprint,
    string Status);

/// <summary>管理端看到的签名密钥。</summary>
public sealed record LicenseSigningKeyDto(
    Guid Id,
    string KeyId,
    string Algorithm,
    string PublicKey,
    string Fingerprint,
    string Status,
    bool IsCurrent,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset CreatedAt);

/// <summary>签名密钥总览：未配置私钥时 <see cref="IsManaged"/> 为 false，客户端一切按 Unmanaged 处理。</summary>
public sealed record LicenseSigningKeyStatusDto(
    bool IsManaged,
    string? CurrentKeyId,
    string? Fingerprint,
    IReadOnlyList<LicenseSigningKeyDto> Keys);
