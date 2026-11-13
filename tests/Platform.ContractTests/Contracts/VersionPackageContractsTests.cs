using System.Text.Json;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Kernel.Errors;

namespace Platform.ContractTests.Contracts;

/// <summary>
/// 发布包契约：错误码段位/HTTP 映射稳定，新增可选字段不破坏旧客户端的反序列化。
/// </summary>
public sealed class VersionPackageContractsTests
{
    private static readonly ApiErrorCode[] PackageCodes =
    [
        ApiErrorCode.VersionsPackageNotFound,
        ApiErrorCode.VersionsPackageNotReady,
        ApiErrorCode.VersionsPackageManifestMissing,
        ApiErrorCode.VersionsPackageAppMismatch,
        ApiErrorCode.VersionsPackageChannelVersionMismatch,
        ApiErrorCode.VersionsPackageFileNameMismatch,
        ApiErrorCode.VersionsPackageManifestConflict,
        ApiErrorCode.VersionsPackageUnsafeArchive,
        ApiErrorCode.VersionsPackageTooLarge,
        ApiErrorCode.VersionsReleaseImmutable,
        ApiErrorCode.VersionsUpdaterNotFound,
        ApiErrorCode.VersionsPackageKindUnsupported,
        ApiErrorCode.VersionsPackageIngestionFailed,
        ApiErrorCode.VersionsPackageStatusInvalid,
        ApiErrorCode.VersionsStorageInsufficient,
        ApiErrorCode.VersionsPackageManifestInvalid,
    ];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 发布包错误码全部落在版本段()
    {
        foreach (var code in PackageCodes)
        {
            Assert.Equal(ApiErrorSegments.Versions, ApiErrorSegments.Of(code));
            Assert.Equal(PlatformModuleKeys.Versions, ApiErrorSegments.ModuleKeyOf(code));
        }
    }

    [Theory]
    [InlineData(ApiErrorCode.VersionsPackageNotFound, 404)]
    [InlineData(ApiErrorCode.VersionsPackageNotReady, 409)]
    [InlineData(ApiErrorCode.VersionsPackageManifestMissing, 422)]
    [InlineData(ApiErrorCode.VersionsPackageAppMismatch, 422)]
    [InlineData(ApiErrorCode.VersionsPackageChannelVersionMismatch, 422)]
    [InlineData(ApiErrorCode.VersionsPackageFileNameMismatch, 422)]
    [InlineData(ApiErrorCode.VersionsPackageManifestConflict, 409)]
    [InlineData(ApiErrorCode.VersionsPackageUnsafeArchive, 422)]
    [InlineData(ApiErrorCode.VersionsPackageTooLarge, 413)]
    [InlineData(ApiErrorCode.VersionsReleaseImmutable, 409)]
    [InlineData(ApiErrorCode.VersionsUpdaterNotFound, 404)]
    [InlineData(ApiErrorCode.VersionsPackageKindUnsupported, 400)]
    [InlineData(ApiErrorCode.VersionsPackageIngestionFailed, 422)]
    [InlineData(ApiErrorCode.VersionsPackageStatusInvalid, 409)]
    [InlineData(ApiErrorCode.VersionsStorageInsufficient, 507)]
    [InlineData(ApiErrorCode.VersionsPackageManifestInvalid, 422)]
    public void 发布包关键错误码的HTTP映射必须稳定(ApiErrorCode code, int expected) =>
        Assert.Equal(expected, ApiErrorHttpStatus.Map(code));

    [Fact]
    public void 旧版版本摘要响应缺少包字段时使用默认值()
    {
        const string json = """
        {
          "id": "018f0000-0000-7000-8000-000000000001",
          "channelKey": "stable",
          "version": "1.2.3",
          "status": "published",
          "notes": null,
          "artifactCount": 1,
          "fileCount": 2,
          "totalSizeBytes": 1024,
          "publishedAt": "2026-01-01T00:00:00+00:00",
          "createdAt": "2026-01-01T00:00:00+00:00",
          "updatedAt": "2026-01-01T00:00:00+00:00"
        }
        """;

        var summary = JsonSerializer.Deserialize<ReleaseSummaryDto>(json, WebJson);

        Assert.NotNull(summary);
        Assert.Equal("1.2.3", summary.Version);
        Assert.Equal(0, summary.PackageCount);
        Assert.Equal(0, summary.PackageSizeBytes);
    }

    [Fact]
    public void 旧版版本详情响应缺少packages时为null()
    {
        const string json = """
        {
          "id": "018f0000-0000-7000-8000-000000000001",
          "channelKey": "stable",
          "version": "1.2.3",
          "status": "published",
          "notes": null,
          "artifactCount": 0,
          "fileCount": 0,
          "totalSizeBytes": 0,
          "publishedAt": null,
          "archivedAt": null,
          "createdBy": "admin",
          "createdAt": "2026-01-01T00:00:00+00:00",
          "updatedAt": "2026-01-01T00:00:00+00:00",
          "artifacts": []
        }
        """;

        var detail = JsonSerializer.Deserialize<ReleaseDetailDto>(json, WebJson);

        Assert.NotNull(detail);
        Assert.Null(detail.Packages);
        Assert.Empty(detail.Artifacts);
    }

    [Fact]
    public void 旧版检查更新响应缺少包字段仍可反序列化()
    {
        const string json = """
        {
          "hasUpdate": true,
          "channelKey": "stable",
          "currentVersion": "1.2.2",
          "release": {
            "releaseId": "018f0000-0000-7000-8000-000000000001",
            "channelKey": "stable",
            "version": "1.2.3",
            "runtimeIdentifier": "win-x64",
            "manifestHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "publishedAt": "2026-01-01T00:00:00+00:00",
            "files": [
              { "path": "App.exe", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "sizeBytes": 10 }
            ]
          }
        }
        """;

        var response = JsonSerializer.Deserialize<CheckUpdateResponse>(json, WebJson);

        Assert.NotNull(response);
        Assert.True(response.HasUpdate);
        Assert.NotNull(response.Release);
        Assert.Null(response.LatestVersion);
        Assert.False(response.IsDowngrade);
        Assert.Null(response.Installer);
        Assert.Null(response.IncrementalPackage);
        Assert.False(response.HasUpdaterUpdate);
        Assert.Null(response.Updater);
    }

    [Fact]
    public void 新版包字段可完整反序列化()
    {
        const string json = """
        {
          "id": "018f0000-0000-7000-8000-000000000002",
          "releaseId": "018f0000-0000-7000-8000-000000000001",
          "channelKey": "stable",
          "version": "1.2.3",
          "kind": "full",
          "target": "win-x64",
          "format": "exe",
          "fileName": "Setup-stable-1.2.3.exe",
          "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
          "sizeBytes": 2048,
          "status": "ready",
          "manifestSource": "embedded",
          "manifestHash": null,
          "reused": false,
          "errorCode": null,
          "errorMessage": null,
          "completedAt": "2026-01-01T00:00:00+00:00",
          "createdAt": "2026-01-01T00:00:00+00:00",
          "updatedAt": "2026-01-01T00:00:00+00:00"
        }
        """;

        var package = JsonSerializer.Deserialize<ReleasePackageDto>(json, WebJson);

        Assert.NotNull(package);
        Assert.Equal("full", package.Kind);
        Assert.Equal("ready", package.Status);
        Assert.Equal("embedded", package.ManifestSource);
        Assert.Equal(2048, package.SizeBytes);
        Assert.False(package.Reused);
        Assert.Null(package.FileCount);
    }
}
