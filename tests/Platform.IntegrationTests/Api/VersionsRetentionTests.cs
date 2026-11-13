using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 版本域保留策略：过期票据、超期失败包、归档版本，以及 <c>versions/</c> 下的孤儿 Blob。
/// </summary>
public sealed class VersionsRetentionTests : IClassFixture<VersionsRetentionTests.RetentionFactory>
{
    // Blob 的最后写入时间来自真实文件系统，测试时钟必须贴着真实时间起步，宽限期才可判定。
    internal static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddMinutes(-1);

    private readonly RetentionFactory _factory;

    public VersionsRetentionTests(RetentionFactory factory)
    {
        _factory = factory;
    }

    public sealed class RetentionFactory : PlatformApiFactory
    {
        internal TestClock Clock { get; } = new() { UtcNow = Start };

        protected override void ConfigureTestSettings(IDictionary<string, string?> settings)
        {
            settings["Versions:BlobSweepEnabled"] = "true";
            settings["Versions:BlobSweepDryRun"] = "false";
            settings["Versions:BlobSweepGraceHours"] = "0";
            settings["Versions:FailedPackageRetentionDays"] = "30";
            settings["Versions:ArchivedReleaseRetentionDays"] = "30";
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        }
    }

    [Fact]
    public async Task 孤儿清理保留已发布内容并回收失败包与过期票据()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsAdminAsync(client);

        await CreateChannelAsync(client, token, "retention-a");
        var appBytes = "retention-referenced"u8.ToArray();
        var appSha = await UploadAsync(client, token, appBytes);
        var release = await CreateReleaseAsync(
            client, token, "retention-a", "1.0.0", publish: true,
            Artifact("win-x64", ("App.exe", appBytes, appSha)));

        var blobs = _factory.Services.GetRequiredService<IBlobStorage>();
        var appKey = await ResolveAppKeyAsync(_factory.Services.GetRequiredService<IBlobEnumerator>());
        var referencedPath = $"versions/{appKey}/{appSha}";
        Assert.True(await blobs.ExistsAsync(referencedPath));

        // 孤儿内容只被一个「失败包」引用：删行后应由同一轮扫描回收。
        var orphanBytes = "retention-orphan"u8.ToArray();
        var orphanSha = Sha256(orphanBytes);
        var orphanPath = $"versions/{appKey}/{orphanSha}";
        await blobs.WriteAsync(orphanPath, new MemoryStream(orphanBytes));

        var now = _factory.Clock.UtcNow;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Set<ReleasePackage>().Add(new ReleasePackage
            {
                AppKey = appKey,
                ReleaseId = release.Id,
                Kind = VersionConventions.PackageKindFull,
                Target = "any",
                Format = "exe",
                FileName = "Setup-retention-1.0.0.exe",
                Sha256 = orphanSha,
                SizeBytes = orphanBytes.Length,
                ManifestSource = VersionConventions.ManifestSourceManual,
                Status = VersionConventions.PackageStatusFailed,
                Attempts = 1,
                ErrorCode = ApiErrorCode.VersionsPackageIngestionFailed.ToString(),
            });
            db.Set<VersionUploadTicket>().Add(new VersionUploadTicket
            {
                AppKey = appKey,
                Sha256 = orphanSha,
                SizeBytes = orphanBytes.Length,
                ExpiresAt = now.AddDays(-3),
            });
            await db.SaveChangesAsync();
        }

        _factory.Clock.Advance(TimeSpan.FromDays(40));
        var result = await RunRetentionAsync();
        Assert.True(result.RowsDeleted >= 2);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.DoesNotContain(db.Set<ReleasePackage>().ToList(), x => x.Sha256 == orphanSha);
            Assert.DoesNotContain(db.Set<VersionUploadTicket>().ToList(), x => x.Sha256 == orphanSha);
        }

        Assert.True(await blobs.ExistsAsync(referencedPath));
        Assert.False(await blobs.ExistsAsync(orphanPath));

        var metrics = _factory.Services.GetRequiredService<PlatformMetrics>().Render();
        Assert.Contains(PlatformMetricNames.VersionsBlobOrphansDeletedBytes, metrics);
        Assert.Contains(PlatformMetricNames.VersionsUploadTicketsExpired, metrics);
    }

    [Fact]
    public async Task 归档版本超过保留期后连内容一起回收()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsAdminAsync(client);

        await CreateChannelAsync(client, token, "retention-b");
        var appBytes = "retention-archive"u8.ToArray();
        var appSha = await UploadAsync(client, token, appBytes);
        var release = await CreateReleaseAsync(
            client, token, "retention-b", "2.0.0", publish: true,
            Artifact("win-x64", ("App.exe", appBytes, appSha)));

        _ = await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{release.Id}/archive",
                token)));

        var blobs = _factory.Services.GetRequiredService<IBlobStorage>();
        var appKey = await ResolveAppKeyAsync(_factory.Services.GetRequiredService<IBlobEnumerator>());
        var blobPath = $"versions/{appKey}/{appSha}";
        Assert.True(await blobs.ExistsAsync(blobPath));

        _factory.Clock.Advance(TimeSpan.FromDays(40));
        _ = await RunRetentionAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.DoesNotContain(db.Set<Release>().ToList(), x => x.Id == release.Id);
        }

        Assert.False(await blobs.ExistsAsync(blobPath));
    }

    private async Task<RetentionCleanupResult> RunRetentionAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var task = scope.ServiceProvider
            .GetServices<IRetentionCleanupTask>()
            .Single(x => x.Name == "versions");
        return await task.RunAsync();
    }

    private static async Task<string> ResolveAppKeyAsync(IBlobEnumerator enumerator)
    {
        await foreach (var entry in enumerator.EnumerateAsync("versions/"))
        {
            var segments = entry.RelativePath.Split('/');
            if (segments.Length == 3)
            {
                return segments[1];
            }
        }

        throw new InvalidOperationException("未找到已上传内容，无法推断应用键。");
    }

    private static Task<string> LoginAsAdminAsync(HttpClient client) =>
        IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "保留策略测试"))));

    private static async Task<ReleaseDetailDto> CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        bool publish,
        CreateReleaseArtifactRequest artifact) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(channelKey, version, "保留策略测试", publish, [artifact]))));

    private static CreateReleaseArtifactRequest Artifact(
        string runtimeIdentifier,
        params (string Path, byte[] Content, string Sha)[] files) =>
        new(
            runtimeIdentifier,
            [.. files.Select(static file =>
                new ReleaseFileInput(file.Path, file.Sha, file.Content.Length))]);

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Sha256(content);
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
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
        _ = await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        return sha;
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>dry-run 只计数不删除：生产验证一周的默认口径。</summary>
public sealed class VersionsRetentionDryRunTests : IClassFixture<VersionsRetentionDryRunTests.DryRunFactory>
{
    private readonly DryRunFactory _factory;

    public VersionsRetentionDryRunTests(DryRunFactory factory)
    {
        _factory = factory;
    }

    public sealed class DryRunFactory : PlatformApiFactory
    {
        internal TestClock Clock { get; } = new() { UtcNow = VersionsRetentionTests.Start };

        protected override void ConfigureTestSettings(IDictionary<string, string?> settings)
        {
            settings["Versions:BlobSweepEnabled"] = "true";
            settings["Versions:BlobSweepDryRun"] = "true";
            settings["Versions:BlobSweepGraceHours"] = "0";
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        }
    }

    [Fact]
    public async Task DryRun时只记录候选不删除文件()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        _ = await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest("retention-dry", "dry-run", null))));

        var payload = "dry-run-orphan"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var blobs = _factory.Services.GetRequiredService<IBlobStorage>();
        var enumerator = _factory.Services.GetRequiredService<IBlobEnumerator>();

        // 先造一个可推断应用键的引用内容，再写孤儿文件。
        await WriteViaTicketAsync(client, token, "dry-run-referenced"u8.ToArray());
        var appKey = await ResolveAppKeyAsync(enumerator);
        var orphanPath = $"versions/{appKey}/{sha}";
        await blobs.WriteAsync(orphanPath, new MemoryStream(payload));

        _factory.Clock.Advance(TimeSpan.FromHours(1));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var task = scope.ServiceProvider
                .GetServices<IRetentionCleanupTask>()
                .Single(x => x.Name == "versions");
            _ = await task.RunAsync();
        }

        Assert.True(await blobs.ExistsAsync(orphanPath));
        var metrics = _factory.Services.GetRequiredService<PlatformMetrics>().Render();
        Assert.Contains(PlatformMetricNames.VersionsBlobOrphansScanned, metrics);
        Assert.DoesNotContain(PlatformMetricNames.VersionsBlobOrphansDeleted, metrics);
    }

    private static async Task<string> WriteViaTicketAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
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
        _ = await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        return sha;
    }

    private static async Task<string> ResolveAppKeyAsync(IBlobEnumerator enumerator)
    {
        await foreach (var entry in enumerator.EnumerateAsync("versions/"))
        {
            var segments = entry.RelativePath.Split('/');
            if (segments.Length == 3)
            {
                return segments[1];
            }
        }

        throw new InvalidOperationException("未找到已上传内容，无法推断应用键。");
    }
}
