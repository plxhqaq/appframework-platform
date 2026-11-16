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

    /// <summary>应用自定义权限码正则：小写点分，至少两段，不得与平台权限码冲突。</summary>
    public const string AppPermissionCodePattern = "^[a-z][a-z0-9-]*(\\.[a-z0-9-]+)+$";

    /// <summary>单个应用可声明的权限上限，防止清单被当成任意数据存储。</summary>
    public const int MaxAppPermissions = 500;

    /// <summary>单个应用可声明的常用权限组合上限。</summary>
    public const int MaxAppPermissionPresets = 20;

    /// <summary>常用权限组合键正则（应用内唯一）。</summary>
    public const string AppPermissionPresetKeyPattern = "^[a-z][a-z0-9-]{0,63}$";
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
/// <param name="PermissionsLocked">应用权限清单是否已锁定；已锁定则无需再上传。</param>
public sealed record M2MTokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAt,
    string AppKey,
    IReadOnlyList<string> Scopes,
    bool PermissionsLocked = false);

/// <summary>应用自定义权限：由客户端应用自行声明，平台只校验格式与冲突。</summary>
/// <param name="Code">权限码，小写点分且不得占用平台权限码。</param>
/// <param name="DisplayName">显示名（管理台权限树与客户端共用）。</param>
/// <param name="Description">权限说明。</param>
/// <param name="Group">分组键，缺省为 <c>app</c>。</param>
/// <param name="GroupLabel">分组显示名，缺省用应用名。</param>
/// <param name="Sort">管理台权限树排序。</param>
public sealed record AppPermissionDto(
    string Code,
    string DisplayName,
    string? Description = null,
    string? Group = null,
    string? GroupLabel = null,
    int Sort = 0);

/// <summary>应用权限清单（权限 + 常用组合），供管理台渲染与快速选择。</summary>
/// <param name="Permissions">权限定义。</param>
/// <param name="Presets">常用权限组合。</param>
/// <param name="Locked">是否已锁定；锁定后必须人工解锁才能再次上传。</param>
public sealed record AppPermissionManifestDto(
    IReadOnlyList<AppPermissionDto> Permissions,
    IReadOnlyList<AppPermissionPresetDto> Presets,
    bool Locked = false);

/// <summary>应用自定义的常用权限组合（例如作业员、工程师）。</summary>
/// <param name="Key">应用内唯一的组合键。</param>
/// <param name="Label">显示名。</param>
/// <param name="Description">组合说明。</param>
/// <param name="Permissions">组合包含的权限码，必须全部在清单中声明。</param>
/// <param name="Sort">管理台排序。</param>
public sealed record AppPermissionPresetDto(
    string Key,
    string Label,
    string? Description = null,
    IReadOnlyList<string>? Permissions = null,
    int Sort = 0);

/// <summary>替换应用权限清单请求；整表替换，空清单表示清空。</summary>
/// <remarks>首次上传成功后清单自动锁定；之后必须先在管理台解锁才能再次替换。</remarks>
public sealed record ReplaceAppPermissionsRequest(
    IReadOnlyList<AppPermissionDto>? Permissions = null,
    IReadOnlyList<AppPermissionPresetDto>? Presets = null);
