namespace Platform.Contracts;

/// <summary>
/// 平台统一错误码。**分段规则**（新增码只在自己段内追加，已删除的数值不得复用）：
/// <list type="bullet">
/// <item>0–99 通用</item>
/// <item>100–199 身份</item>
/// <item>200–299 版本</item>
/// <item>300–399 授权</item>
/// <item>400–499 备份</item>
/// <item>500–599 文档</item>
/// <item>600–699 应用接入</item>
/// </list>
/// </summary>
/// <remarks>
/// HTTP 状态码不在这里表达：由服务端 <c>ApiErrorHttpStatus.Map</c> 唯一映射，
/// 客户端只认 <see cref="ApiResult{T}.Code"/>，不依赖 HTTP 码做业务分支。
/// </remarks>
public enum ApiErrorCode
{
    // ━━━ 0–99 通用 ━━━
    Ok = 0,
    Unknown = 1,
    ValidationFailed = 2,
    Unauthorized = 3,
    Forbidden = 4,
    NotFound = 5,
    Conflict = 6,
    RateLimited = 7,
    BadRequest = 8,
    NotImplemented = 9,
    DependencyFailure = 10,
    IdempotencyConflict = 11,
    PayloadTooLarge = 12,
    PreconditionFailed = 13,
    UnsupportedMediaType = 14,
    ClientVersionTooLow = 15,
    RequestTimeout = 16,
    ServiceUnavailable = 17,

    // ━━━ 100–199 身份 ━━━
    IdentityUserNotFound = 100,
    IdentityInvalidCredentials = 101,
    IdentityUserDisabled = 102,
    IdentityRoleNotFound = 103,
    IdentityPermissionUnknown = 104,
    IdentitySingleKeyInvalid = 105,
    IdentitySingleKeyExpired = 106,
    IdentitySingleKeyExhausted = 107,
    IdentitySingleKeyDeviceMismatch = 108,
    IdentityQrSessionNotFound = 109,
    IdentityQrSessionExpired = 110,
    IdentityQrSessionAlreadyUsed = 111,
    IdentityTokenExpired = 112,
    IdentityRefreshTokenInvalid = 113,
    IdentityRefreshTokenRevoked = 114,
    IdentityPasswordPolicyViolation = 115,
    IdentityAccountThrottled = 116,
    IdentityRoleInUse = 117,
    IdentityPermissionDenied = 118,

    // ━━━ 200–299 版本 ━━━
    VersionsChannelNotFound = 200,
    VersionsChannelKeyExists = 201,
    VersionsReleaseNotFound = 202,
    VersionsReleaseNotPublished = 203,
    VersionsReleaseVersionExists = 204,
    VersionsArtifactNotFound = 205,
    VersionsManifestMismatch = 206,
    VersionsUploadTicketInvalid = 207,
    VersionsUploadTicketExpired = 208,
    VersionsUploadTicketConsumed = 209,
    VersionsPackageHashMismatch = 210,
    VersionsRuntimeIdentifierUnsupported = 211,
    VersionsNoPublishedRelease = 212,

    // ━━━ 300–399 授权 ━━━
    LicensingProductNotFound = 300,
    LicensingProductKeyExists = 301,
    LicensingEntitlementNotFound = 302,
    LicensingEntitlementExpired = 303,
    LicensingEntitlementRevoked = 304,
    LicensingSigningKeyUnavailable = 305,
    LicensingOfflineCodeInvalid = 306,
    LicensingOfflineCodeMachineMismatch = 307,
    LicensingMachineCodeMissing = 308,
    LicensingSeatExhausted = 309,
    LicensingPayloadUnsupported = 310,

    // ━━━ 400–499 备份 ━━━
    BackupsNamespaceNotFound = 400,
    BackupsNamespaceKeyExists = 401,
    BackupsNotFound = 402,
    BackupsSchemaIncompatible = 403,
    BackupsChannelMismatch = 404,
    BackupsPayloadInvalid = 405,
    BackupsVersionQuotaExceeded = 406,
    BackupsPushRejected = 407,
    BackupsAckTimeout = 408,

    // ━━━ 500–599 文档 ━━━
    DocumentsCollectionNotFound = 500,
    DocumentsCollectionKeyExists = 501,
    DocumentsNotFound = 502,
    DocumentsRevisionNotFound = 503,
    DocumentsVisibilityDenied = 504,
    DocumentsFileMissing = 505,

    // ━━━ 600–699 应用接入 ━━━
    AppsNotFound = 600,
    AppsKeyExists = 601,
    AppsDisabled = 602,
    AppsApiKeyInvalid = 603,
    AppsApiKeyExpired = 604,
    AppsApiKeyRevoked = 605,
    AppsApiKeyScopeDenied = 606,
    AppsQuotaExceeded = 607,
    AppsApiKeyScopeUnknown = 608,
    AppsApiKeyNotFound = 609,
}

/// <summary>错误码分段边界。用于分组展示、CI 断言与客户端提示文案归类。</summary>
public static class ApiErrorSegments
{
    public const int Common = 0;
    public const int Identity = 100;
    public const int Versions = 200;
    public const int Licensing = 300;
    public const int Backups = 400;
    public const int Documents = 500;
    public const int Apps = 600;

    /// <summary>全部段起点，升序。</summary>
    public static IReadOnlyList<int> Starts { get; } = [Common, Identity, Versions, Licensing, Backups, Documents, Apps];

    /// <summary>返回错误码所属段的起点（未知段返回 <see cref="Common"/>）。</summary>
    public static int Of(ApiErrorCode code)
    {
        var value = (int)code;
        var start = Common;
        foreach (var candidate in Starts)
        {
            if (value >= candidate) start = candidate;
        }
        return start;
    }

    /// <summary>返回错误码所属段的模块键（通用段返回 <c>common</c>）。</summary>
    public static string ModuleKeyOf(ApiErrorCode code) => Of(code) switch
    {
        Identity => PlatformModuleKeys.Identity,
        Versions => PlatformModuleKeys.Versions,
        Licensing => PlatformModuleKeys.Licensing,
        Backups => PlatformModuleKeys.Backups,
        Documents => PlatformModuleKeys.Documents,
        Apps => PlatformModuleKeys.Apps,
        _ => "common",
    };
}
