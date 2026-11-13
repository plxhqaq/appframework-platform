using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Services;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 增量包异步摄取：成功落清单并自动发布、各类失败归因与重试、坏归档拒绝、
/// 崩溃租约回收与并发认领。Worker 循环在测试里关闭，用例直接驱动服务。
/// </summary>
public sealed class IncrementalIngestionTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public IncrementalIngestionTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 增量包摄取后落清单并按请求自动发布()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "inc-success";

        await CreateChannelAsync(client, token, channelKey);

        var payload = "incremental-payload-v5"u8.ToArray();
        var payloadSha = Sha256(payload);
        var packageBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "5.0.0", ("App.exe", payloadSha, payload.Length))),
            ("App.exe", payload));
        var packageSha = await UploadAsync(client, token, packageBytes);

        var registered = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            packageSha,
            packageBytes.Length,
            "Update-inc-success-5.0.0.zip",
            VersionConventions.PackageKindIncremental,
            Target: "win-x64",
            Format: "zip",
            ChannelKey: channelKey,
            Version: "5.0.0",
            Publish: true));

        Assert.Equal(VersionConventions.PackageStatusPending, registered.Status);
        Assert.Equal(1, registered.FileCount);

        var processed = await ProcessNextAsync();
        Assert.NotNull(processed);
        Assert.Equal(registered.Id, processed!.Id);
        Assert.True(
            processed.ErrorCode is null,
            $"摄取失败：{processed.ErrorCode} {processed.ErrorMessage}");
        Assert.Equal(VersionConventions.PackageStatusReady, processed.Status);
        Assert.Null(processed.ErrorCode);
        Assert.Equal(1, processed.FileCount);
        Assert.NotNull(processed.CompletedAt);

        var detail = await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/releases/{registered.ReleaseId}",
                token)));
        Assert.Equal(VersionConventions.StatusPublished, detail.Status);
        var artifact = Assert.Single(detail.Artifacts);
        Assert.Equal("win-x64", artifact.RuntimeIdentifier);
        Assert.Equal(registered.ManifestHash, artifact.ManifestHash);
        Assert.Equal(1, artifact.FileCount);
        var file = Assert.Single(artifact.Files);
        Assert.Equal("App.exe", file.Path);
        Assert.Equal(payloadSha, file.Sha256);
        Assert.Equal(payload.Length, file.SizeBytes);

        var check = await CheckAsync(client, token, channelKey, "4.0.0");
        Assert.True(check.HasUpdate);
        Assert.Equal("5.0.0", check.LatestVersion);
        Assert.NotNull(check.Release);
        Assert.Equal(registered.ManifestHash, check.Release!.ManifestHash);
        Assert.Null(check.Installer);
        Assert.NotNull(check.IncrementalPackage);
        Assert.Equal(registered.Id, check.IncrementalPackage!.Id);
        Assert.Equal(packageSha, check.IncrementalPackage.Sha256);

        // 已是最新时不下发增量包，只保留清单供完整性修复
        var upToDate = await CheckAsync(client, token, channelKey, "5.0.0");
        Assert.False(upToDate.HasUpdate);
        Assert.Null(upToDate.IncrementalPackage);

        var packageDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token));
        Assert.Equal(HttpStatusCode.OK, packageDownload.StatusCode);
        Assert.Equal(packageBytes, await packageDownload.Content.ReadAsByteArrayAsync());

        var fileDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/download/{payloadSha}",
            token));
        Assert.Equal(HttpStatusCode.OK, fileDownload.StatusCode);
        Assert.Equal(payload, await fileDownload.Content.ReadAsByteArrayAsync());

        // 没有待处理包时返回 null（重复驱动幂等）
        Assert.Null(await ProcessNextAsync());
    }

    [Fact]
    public async Task 摄取失败按原因归因且重试仅限failed()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "inc-fail";

        await CreateChannelAsync(client, token, channelKey);

        var good = "right-payload"u8.ToArray();
        var goodSha = Sha256(good);
        var wrong = "wrong-payload"u8.ToArray();
        Assert.Equal(good.Length, wrong.Length);

        // 1) 包内缺清单文件
        var missingBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "6.0.1", ("Missing.dll", goodSha, good.Length))));
        var missing = await RegisterVersionAsync(client, token, channelKey, "6.0.1", missingBytes);
        var missingResult = await ProcessNextAsync();
        Assert.Equal(VersionConventions.PackageStatusFailed, missingResult!.Status);
        Assert.Equal(nameof(ApiErrorCode.VersionsPackageIngestionFailed), missingResult.ErrorCode);
        Assert.Contains("缺少清单文件", missingResult.ErrorMessage);
        await AssertNoArtifactsAsync(client, token, missing.ReleaseId);

        // 2) 包内多出清单外文件
        var extraBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "6.0.2", ("App.exe", goodSha, good.Length))),
            ("App.exe", good),
            ("extra.bin", "unexpected"u8.ToArray()));
        _ = await RegisterVersionAsync(client, token, channelKey, "6.0.2", extraBytes);
        var extraResult = await ProcessNextAsync();
        Assert.Equal(VersionConventions.PackageStatusFailed, extraResult!.Status);
        Assert.Equal(nameof(ApiErrorCode.VersionsPackageIngestionFailed), extraResult.ErrorCode);
        Assert.Contains("清单外", extraResult.ErrorMessage);

        // 3) 内容哈希与清单不符
        var hashBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "6.0.3", ("App.exe", goodSha, good.Length))),
            ("App.exe", wrong));
        _ = await RegisterVersionAsync(client, token, channelKey, "6.0.3", hashBytes);
        var hashResult = await ProcessNextAsync();
        Assert.Equal(VersionConventions.PackageStatusFailed, hashResult!.Status);
        Assert.Equal(nameof(ApiErrorCode.VersionsPackageHashMismatch), hashResult.ErrorCode);

        // failed 可重试回 pending；但仍会因同一原因再次失败
        var retried = await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/packages/{missing.Id}/retry",
                token)));
        Assert.Equal(VersionConventions.PackageStatusPending, retried.Status);
        Assert.Null(retried.ErrorCode);
        var failedAgain = await ProcessNextAsync();
        Assert.Equal(VersionConventions.PackageStatusFailed, failedAgain!.Status);

        // 未失败的包不能重试
        var pendingBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "6.0.9", ("App.exe", goodSha, good.Length))),
            ("App.exe", good));
        var pending = await RegisterVersionAsync(client, token, channelKey, "6.0.9", pendingBytes);
        var refused = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/versions/packages/{pending.Id}/retry",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageStatusInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(refused, HttpStatusCode.Conflict));

        // 收尾：把剩余包处理掉，避免影响后续用例
        Assert.NotNull(await ProcessNextAsync());
    }

    [Fact]
    public async Task 坏归档在登记时拒绝且不产生包行()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "inc-archive";

        await CreateChannelAsync(client, token, channelKey);
        var payload = "archive-payload"u8.ToArray();
        var payloadSha = Sha256(payload);

        // 路径穿越条目
        var traversal = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "7.0.1", ("App.exe", payloadSha, payload.Length))),
            ("../evil.txt", payload));
        var traversalResponse = await RegisterRawAsync(
            client, token, channelKey, "7.0.1", traversal);
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageUnsafeArchive,
            await IdentityTestHelpers.ReadErrorCodeAsync(traversalResponse, HttpStatusCode.UnprocessableEntity));

        // 高压缩比（zip bomb）
        var bombPayload = new byte[4 * 1024 * 1024];
        var bombBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "7.0.2", ("App.exe", Sha256(bombPayload), bombPayload.Length))),
            ("App.exe", bombPayload));
        var bombResponse = await RegisterRawAsync(client, token, channelKey, "7.0.2", bombBytes);
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageUnsafeArchive,
            await IdentityTestHelpers.ReadErrorCodeAsync(bombResponse, HttpStatusCode.UnprocessableEntity));

        // 合法 zip 但缺 af-package.json：仍按「缺清单」拒绝
        var noManifest = BuildZip(("App.exe", payload));
        var noManifestResponse = await RegisterRawAsync(
            client, token, channelKey, "7.0.4", noManifest);
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestMissing,
            await IdentityTestHelpers.ReadErrorCodeAsync(noManifestResponse, HttpStatusCode.UnprocessableEntity));

        // 完全不是 zip
        var corrupt = "this-is-not-a-zip-archive"u8.ToArray();
        var corruptResponse = await RegisterRawAsync(client, token, channelKey, "7.0.3", corrupt);
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(corruptResponse, HttpStatusCode.UnprocessableEntity));

        var listed = await ReadDataAsync<PageResult<ReleasePackageDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/packages?channelKey={channelKey}&pageSize=50",
                token)));
        Assert.Empty(listed.Items);
    }

    [Fact]
    public async Task 崩溃租约可回收且并发只有一个认领者()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "inc-worker";

        await CreateChannelAsync(client, token, channelKey);

        var payload = "worker-payload"u8.ToArray();
        var payloadSha = Sha256(payload);
        var packageBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "8.0.0", ("App.exe", payloadSha, payload.Length))),
            ("App.exe", payload));
        var registered = await RegisterVersionAsync(client, token, channelKey, "8.0.0", packageBytes);

        // 模拟 Worker 崩溃：processing + 租约过期 + 已尝试一次
        await UseDbAsync(async db =>
        {
            var package = await db.Set<ReleasePackage>().SingleAsync(x => x.Id == registered.Id);
            package.Status = VersionConventions.PackageStatusProcessing;
            package.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5);
            package.LockedBy = "crashed-worker";
            package.Attempts = 1;
            await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
            Assert.Equal(1, await ingestion.RecoverStaleAsync());
        }

        var results = await Task.WhenAll(ProcessNextAsync(), ProcessNextAsync());
        var claimed = results.Where(static x => x is not null).ToList();
        var processed = Assert.Single(claimed);
        Assert.Equal(VersionConventions.PackageStatusReady, processed!.Status);

        // 尝试次数耗尽后回收直接置 failed
        var exhaustedBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, "8.0.1", ("App.exe", payloadSha, payload.Length))),
            ("App.exe", payload));
        var exhausted = await RegisterVersionAsync(client, token, channelKey, "8.0.1", exhaustedBytes);
        await UseDbAsync(async db =>
        {
            var package = await db.Set<ReleasePackage>().SingleAsync(x => x.Id == exhausted.Id);
            package.Status = VersionConventions.PackageStatusProcessing;
            package.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5);
            package.LockedBy = "crashed-worker";
            package.Attempts = 3;
            await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
            Assert.Equal(1, await ingestion.RecoverStaleAsync());
        }

        var failed = await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/packages/{exhausted.Id}",
                token)));
        Assert.Equal(VersionConventions.PackageStatusFailed, failed.Status);
        Assert.Equal(nameof(ApiErrorCode.VersionsPackageIngestionFailed), failed.ErrorCode);
    }

    private static string IncrementalManifest(
        string channelKey,
        string version,
        params (string Path, string Sha, int Size)[] files)
    {
        var entries = string.Join(
            ",\n      ",
            files.Select(static file =>
                $"{{ \"path\": \"{file.Path}\", \"sha256\": \"{file.Sha}\", \"sizeBytes\": {file.Size} }}"));
        return $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "channelKey": "{{channelKey}}",
          "version": "{{version}}",
          "runtimeIdentifier": "win-x64",
          "files": [
              {{entries}}
          ]
        }
        """;
    }

    private static byte[] BuildZip(params (string Name, object Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content is string text ? Encoding.UTF8.GetBytes(text) : (byte[])content);
            }
        }

        return buffer.ToArray();
    }

    private async Task<ReleasePackageDto?> ProcessNextAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
        return await ingestion.ProcessNextAsync();
    }

    private async Task UseDbAsync(Func<PlatformDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await action(db);
    }

    private static async Task AssertNoArtifactsAsync(HttpClient client, string token, Guid releaseId)
    {
        var detail = await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/releases/{releaseId}",
                token)));
        Assert.Empty(detail.Artifacts);
    }

    private static async Task<ReleasePackageDto> RegisterVersionAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        byte[] packageBytes)
    {
        var sha = await UploadAsync(client, token, packageBytes);
        return await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            sha,
            packageBytes.Length,
            $"Update-{channelKey}-{version}.zip",
            VersionConventions.PackageKindIncremental,
            Target: "win-x64",
            Format: "zip",
            ChannelKey: channelKey,
            Version: version));
    }

    private static async Task<HttpResponseMessage> RegisterRawAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        byte[] packageBytes)
    {
        var sha = await UploadAsync(client, token, packageBytes);
        return await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                sha,
                packageBytes.Length,
                $"Update-{channelKey}-{version}.zip",
                VersionConventions.PackageKindIncremental,
                Target: "win-x64",
                Format: "zip",
                ChannelKey: channelKey,
                Version: version)));
    }

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key) =>
        await ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "集成测试"))));

    private static async Task<ReleasePackageDto> RegisterAsync(
        HttpClient client,
        string token,
        RegisterReleasePackageRequest request) =>
        await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/packages",
                token,
                request)));

    private static async Task<CheckUpdateResponse> CheckAsync(
        HttpClient client,
        string token,
        string channelKey,
        string? currentVersion) =>
        await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(channelKey, currentVersion, "win-x64"))));

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Sha256(content);
        var ticket = await ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, content.Length))));

        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var result = await ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        Assert.Equal(sha, result.Sha256);
        return sha;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) =>
        await IdentityTestHelpers.ReadDataAsync<T>(response);

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
