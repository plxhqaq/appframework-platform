using Platform.Contracts;
using Platform.Kernel.Errors;

namespace Platform.ContractTests.Contracts;

/// <summary>错误码契约：分段、唯一性、HTTP 映射完整性。任何一条破了都是跨端破坏性变更。</summary>
public sealed class ApiErrorCodeTests
{
    private static readonly ApiErrorCode[] AllCodes = [.. Enum.GetValues<ApiErrorCode>()];

    [Fact]
    public void 错误码不得重复()
    {
        var duplicated = AllCodes
            .GroupBy(static c => (int)c)
            .Where(static g => g.Count() > 1)
            .Select(static g => $"错误码值 {g.Key} 被 {g.Count()} 个成员共用")
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void 错误码必须落在已定义的段内()
    {
        foreach (var code in AllCodes)
        {
            var value = (int)code;
            Assert.True(value >= 0 && value <= 699, $"{code} = {value} 超出 0..699 段位预算。");
        }
    }

    [Fact]
    public void 各模块错误码必须落在自己的分段()
    {
        Assert.Equal(ApiErrorSegments.Identity, ApiErrorSegments.Of(ApiErrorCode.IdentityUserNotFound));
        Assert.Equal(ApiErrorSegments.Versions, ApiErrorSegments.Of(ApiErrorCode.VersionsReleaseNotFound));
        Assert.Equal(ApiErrorSegments.Licensing, ApiErrorSegments.Of(ApiErrorCode.LicensingEntitlementRevoked));
        Assert.Equal(ApiErrorSegments.Backups, ApiErrorSegments.Of(ApiErrorCode.BackupsSchemaIncompatible));
        Assert.Equal(ApiErrorSegments.Documents, ApiErrorSegments.Of(ApiErrorCode.DocumentsVisibilityDenied));
        Assert.Equal(ApiErrorSegments.Apps, ApiErrorSegments.Of(ApiErrorCode.AppsApiKeyRevoked));
        Assert.Equal(ApiErrorSegments.Common, ApiErrorSegments.Of(ApiErrorCode.ValidationFailed));
    }

    [Fact]
    public void 分段模块键必须与模块键常量一致()
    {
        Assert.Equal(PlatformModuleKeys.Identity, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.IdentityUserNotFound));
        Assert.Equal(PlatformModuleKeys.Versions, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.VersionsChannelNotFound));
        Assert.Equal(PlatformModuleKeys.Licensing, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.LicensingProductNotFound));
        Assert.Equal(PlatformModuleKeys.Backups, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.BackupsNotFound));
        Assert.Equal(PlatformModuleKeys.Documents, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.DocumentsNotFound));
        Assert.Equal(PlatformModuleKeys.Apps, ApiErrorSegments.ModuleKeyOf(ApiErrorCode.AppsNotFound));
        Assert.Equal("common", ApiErrorSegments.ModuleKeyOf(ApiErrorCode.Unknown));
    }

    [Fact]
    public void Ok必须映射200且其余错误码映射到4xx或5xx()
    {
        Assert.Equal(200, ApiErrorHttpStatus.Map(ApiErrorCode.Ok));

        foreach (var code in AllCodes.Where(static c => c != ApiErrorCode.Ok))
        {
            var status = ApiErrorHttpStatus.Map(code);
            Assert.True(
                status is >= 400 and <= 599,
                $"{code} 映射到 {status}；失败码不得映射成 2xx/3xx（客户端会当成成功）。");
        }
    }

    [Fact]
    public void 关键错误码的HTTP映射必须稳定()
    {
        Assert.Equal(400, ApiErrorHttpStatus.Map(ApiErrorCode.ValidationFailed));
        Assert.Equal(401, ApiErrorHttpStatus.Map(ApiErrorCode.IdentityInvalidCredentials));
        Assert.Equal(403, ApiErrorHttpStatus.Map(ApiErrorCode.IdentityUserDisabled));
        Assert.Equal(404, ApiErrorHttpStatus.Map(ApiErrorCode.VersionsReleaseNotFound));
        Assert.Equal(409, ApiErrorHttpStatus.Map(ApiErrorCode.LicensingSeatExhausted));
        Assert.Equal(429, ApiErrorHttpStatus.Map(ApiErrorCode.IdentityAccountThrottled));
        Assert.Equal(503, ApiErrorHttpStatus.Map(ApiErrorCode.LicensingSigningKeyUnavailable));
        Assert.Equal(500, ApiErrorHttpStatus.Map(ApiErrorCode.Unknown));
    }
}
