using Microsoft.AspNetCore.Http;
using Platform.Contracts;

namespace Platform.Kernel.Errors;

/// <summary>
/// 错误码 → HTTP 状态码的**唯一映射表**。
/// 客户端业务分支只看 <see cref="ApiResult{T}.Code"/>；HTTP 码只用于网关/浏览器/监控的粗粒度分流，
/// 因此这里必须集中一处，禁止在控制器里散写 <c>Results.BadRequest()</c>。
/// </summary>
public static class ApiErrorHttpStatus
{
    /// <summary>映射到 HTTP 状态码。</summary>
    public static int Map(ApiErrorCode code) => code switch
    {
        // ━━━ 通用 ━━━
        ApiErrorCode.Ok => StatusCodes.Status200OK,
        ApiErrorCode.Unknown => StatusCodes.Status500InternalServerError,
        ApiErrorCode.ValidationFailed => StatusCodes.Status400BadRequest,
        ApiErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
        ApiErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ApiErrorCode.NotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.Conflict => StatusCodes.Status409Conflict,
        ApiErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
        ApiErrorCode.BadRequest => StatusCodes.Status400BadRequest,
        ApiErrorCode.NotImplemented => StatusCodes.Status501NotImplemented,
        ApiErrorCode.DependencyFailure => StatusCodes.Status502BadGateway,
        ApiErrorCode.IdempotencyConflict => StatusCodes.Status409Conflict,
        ApiErrorCode.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ApiErrorCode.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ApiErrorCode.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ApiErrorCode.ClientVersionTooLow => StatusCodes.Status426UpgradeRequired,
        ApiErrorCode.RequestTimeout => StatusCodes.Status408RequestTimeout,
        ApiErrorCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,

        // ━━━ 身份 ━━━
        ApiErrorCode.IdentityUserNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.IdentityInvalidCredentials => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentityUserDisabled => StatusCodes.Status403Forbidden,
        ApiErrorCode.IdentityRoleNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.IdentityPermissionUnknown => StatusCodes.Status400BadRequest,
        ApiErrorCode.IdentitySingleKeyInvalid => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentitySingleKeyExpired => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentitySingleKeyExhausted => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentitySingleKeyDeviceMismatch => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentityQrSessionNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.IdentityQrSessionExpired => StatusCodes.Status410Gone,
        ApiErrorCode.IdentityQrSessionAlreadyUsed => StatusCodes.Status409Conflict,
        ApiErrorCode.IdentityTokenExpired => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentityRefreshTokenInvalid => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentityRefreshTokenRevoked => StatusCodes.Status401Unauthorized,
        ApiErrorCode.IdentityPasswordPolicyViolation => StatusCodes.Status400BadRequest,
        ApiErrorCode.IdentityAccountThrottled => StatusCodes.Status429TooManyRequests,
        ApiErrorCode.IdentityRoleInUse => StatusCodes.Status409Conflict,
        ApiErrorCode.IdentityPermissionDenied => StatusCodes.Status403Forbidden,

        // ━━━ 版本 ━━━
        ApiErrorCode.VersionsChannelNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.VersionsChannelKeyExists => StatusCodes.Status409Conflict,
        ApiErrorCode.VersionsReleaseNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.VersionsReleaseNotPublished => StatusCodes.Status409Conflict,
        ApiErrorCode.VersionsReleaseVersionExists => StatusCodes.Status409Conflict,
        ApiErrorCode.VersionsArtifactNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.VersionsManifestMismatch => StatusCodes.Status422UnprocessableEntity,
        ApiErrorCode.VersionsUploadTicketInvalid => StatusCodes.Status400BadRequest,
        ApiErrorCode.VersionsUploadTicketExpired => StatusCodes.Status410Gone,
        ApiErrorCode.VersionsUploadTicketConsumed => StatusCodes.Status409Conflict,
        ApiErrorCode.VersionsPackageHashMismatch => StatusCodes.Status422UnprocessableEntity,
        ApiErrorCode.VersionsRuntimeIdentifierUnsupported => StatusCodes.Status400BadRequest,
        ApiErrorCode.VersionsNoPublishedRelease => StatusCodes.Status404NotFound,

        // ━━━ 授权 ━━━
        ApiErrorCode.LicensingProductNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.LicensingProductKeyExists => StatusCodes.Status409Conflict,
        ApiErrorCode.LicensingEntitlementNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.LicensingEntitlementExpired => StatusCodes.Status403Forbidden,
        ApiErrorCode.LicensingEntitlementRevoked => StatusCodes.Status403Forbidden,
        ApiErrorCode.LicensingSigningKeyUnavailable => StatusCodes.Status503ServiceUnavailable,
        ApiErrorCode.LicensingOfflineCodeInvalid => StatusCodes.Status422UnprocessableEntity,
        ApiErrorCode.LicensingOfflineCodeMachineMismatch => StatusCodes.Status422UnprocessableEntity,
        ApiErrorCode.LicensingMachineCodeMissing => StatusCodes.Status400BadRequest,
        ApiErrorCode.LicensingSeatExhausted => StatusCodes.Status409Conflict,
        ApiErrorCode.LicensingPayloadUnsupported => StatusCodes.Status422UnprocessableEntity,

        // ━━━ 备份 ━━━
        ApiErrorCode.BackupsNamespaceNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.BackupsNamespaceKeyExists => StatusCodes.Status409Conflict,
        ApiErrorCode.BackupsNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.BackupsSchemaIncompatible => StatusCodes.Status422UnprocessableEntity,
        ApiErrorCode.BackupsChannelMismatch => StatusCodes.Status409Conflict,
        ApiErrorCode.BackupsPayloadInvalid => StatusCodes.Status400BadRequest,
        ApiErrorCode.BackupsVersionQuotaExceeded => StatusCodes.Status409Conflict,
        ApiErrorCode.BackupsPushRejected => StatusCodes.Status409Conflict,
        ApiErrorCode.BackupsAckTimeout => StatusCodes.Status504GatewayTimeout,

        // ━━━ 文档 ━━━
        ApiErrorCode.DocumentsCollectionNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.DocumentsCollectionKeyExists => StatusCodes.Status409Conflict,
        ApiErrorCode.DocumentsNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.DocumentsRevisionNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.DocumentsVisibilityDenied => StatusCodes.Status403Forbidden,
        ApiErrorCode.DocumentsFileMissing => StatusCodes.Status404NotFound,

        // ━━━ 应用接入 ━━━
        ApiErrorCode.AppsNotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.AppsKeyExists => StatusCodes.Status409Conflict,
        ApiErrorCode.AppsDisabled => StatusCodes.Status403Forbidden,
        ApiErrorCode.AppsApiKeyInvalid => StatusCodes.Status401Unauthorized,
        ApiErrorCode.AppsApiKeyExpired => StatusCodes.Status401Unauthorized,
        ApiErrorCode.AppsApiKeyRevoked => StatusCodes.Status401Unauthorized,
        ApiErrorCode.AppsApiKeyScopeDenied => StatusCodes.Status403Forbidden,
        ApiErrorCode.AppsQuotaExceeded => StatusCodes.Status429TooManyRequests,

        _ => StatusCodes.Status500InternalServerError,
    };
}
