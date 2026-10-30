namespace Platform.Contracts.Apps;

/// <summary>应用接入契约。应用键是全部业务数据的隔离轴，客户端不得自行传 appId。</summary>
public static class AppConventions
{
    /// <summary>应用键正则：小写字母开头，2–64 位小写字母/数字/连字符。</summary>
    public const string AppKeyPattern = "^[a-z][a-z0-9-]{1,63}$";

    /// <summary>API Key 明文前缀。便于日志脱敏与人工识别，不承载任何秘密。</summary>
    public const string ApiKeyPrefix = "afk_";

    /// <summary>API Key 中 prefix 段长度（十六进制字符数）。</summary>
    public const int ApiKeyPrefixLength = 12;

    /// <summary>API Key 明文最大长度（prefix + 分隔符 + secret）。</summary>
    public const int ApiKeyMaxLength = 128;
}

/// <summary>应用概要。</summary>
public sealed record AppSummaryDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>应用详情。</summary>
public sealed record AppDetailDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    string? SettingsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>创建应用请求。</summary>
public sealed record CreateAppRequest(
    string Key,
    string Name,
    string? Description = null);

/// <summary>更新应用请求。字段为空表示不修改。</summary>
public sealed record UpdateAppRequest(
    string? Name = null,
    string? Description = null,
    bool? IsEnabled = null);

/// <summary>API Key 概要（永不包含 secret 或其哈希）。</summary>
public sealed record ApiKeySummaryDto(
    Guid Id,
    string Prefix,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt,
    bool IsActive);

/// <summary>签发 API Key 请求。</summary>
/// <param name="Name">用途说明。</param>
/// <param name="Scopes">授予的权限码；为空表示仅本应用只读。</param>
/// <param name="ExpiresAt">过期时间；为空表示不过期。</param>
public sealed record CreateApiKeyRequest(
    string? Name = null,
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>新签发的 API Key。**明文只在此响应中出现一次**，服务端只保存哈希。</summary>
public sealed record CreatedApiKeyDto(
    Guid Id,
    string Prefix,
    string Plaintext,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>M2M 换令牌请求。应用键与 API Key 优先从请求头读取，body 仅用于显式声明所需 scope 子集。</summary>
public sealed record M2MTokenRequest(IReadOnlyList<string>? Scopes = null);

/// <summary>M2M 令牌响应。</summary>
public sealed record M2MTokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAt,
    string AppKey,
    IReadOnlyList<string> Scopes);
