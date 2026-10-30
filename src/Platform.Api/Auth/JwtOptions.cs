namespace Platform.Api.Auth;

/// <summary>JWT 配置（<c>Jwt</c> 节点）。</summary>
public sealed class JwtOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Jwt";

    /// <summary>签发者。</summary>
    public string Issuer { get; set; } = "appframework-platform";

    /// <summary>受众。</summary>
    public string Audience { get; set; } = "appframework";

    /// <summary>
    /// HMAC 签名密钥。**至少 32 字符**；生产环境只从环境变量 / 密钥管理服务注入，不入库、不进仓库。
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>访问令牌有效期（分钟）。</summary>
    public int AccessTokenMinutes { get; set; } = 30;

    /// <summary>刷新令牌有效期（天）。S1 落地刷新令牌后生效。</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// 是否开启开发令牌端点（<c>POST /api/v1/dev/token</c>）。
    /// 仅在 Development 且此开关为 <c>true</c> 时注册，生产必须为 <c>false</c>。
    /// </summary>
    public bool AllowDevTokenEndpoint { get; set; }
}
