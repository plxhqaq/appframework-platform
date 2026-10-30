namespace Platform.Modules.Licensing.Options;

/// <summary>授权模块配置。</summary>
/// <remarks>
/// <b>私钥只从部署配置/环境变量读取</b>（<c>Licensing__SigningKeyPkcs8</c>，Base64 PKCS#8），
/// 不落库、不写日志；未配置时模块整体为 <c>Unmanaged</c>，不签发任何快照/离线码。
/// </remarks>
public sealed class LicensingOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Licensing";

    /// <summary>签发私钥（ECDSA P-256，PKCS#8 Base64）。</summary>
    public string? SigningKeyPkcs8 { get; set; }

    /// <summary>单次签发允许的最大授权天数。</summary>
    public int MaxLicenseDays { get; set; } = 3650;

    /// <summary>是否已启用授权（配置了私钥且格式可用）。</summary>
    public bool IsManaged => !string.IsNullOrWhiteSpace(SigningKeyPkcs8);
}
