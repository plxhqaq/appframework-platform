using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Abstractions;

namespace Platform.IntegrationTests.Api;

/// <summary>上传票据的磁盘保护：剩余空间不足时在建票据阶段就拒绝。</summary>
public sealed class VersionsStorageGuardTests : IClassFixture<VersionsStorageGuardTests.NoSpaceFactory>
{
    private readonly NoSpaceFactory _factory;

    public VersionsStorageGuardTests(NoSpaceFactory factory)
    {
        _factory = factory;
    }

    public sealed class NoSpaceFactory : PlatformApiFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IStorageCapacity>();
            services.AddSingleton<IStorageCapacity, NoSpaceCapacity>();
        }

        private sealed class NoSpaceCapacity : IStorageCapacity
        {
            public Task<long> GetAvailableFreeBytesAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(0L);
        }
    }

    [Fact]
    public async Task 剩余磁盘不足时申请票据返回227()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var payload = "storage-guard"u8.ToArray();

        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/uploads",
            token,
            new CreateUploadTicketRequest(Sha256(payload), payload.Length)));

        Assert.Equal(
            (int)ApiErrorCode.VersionsStorageInsufficient,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.InsufficientStorage));
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>登记链路只认包上限：票据按较大值放行，登记再按 <c>MaxPackageSizeBytes</c> 收口。</summary>
public sealed class VersionsPackageSizeLimitTests : IClassFixture<VersionsPackageSizeLimitTests.SmallPackageFactory>
{
    private readonly SmallPackageFactory _factory;

    public VersionsPackageSizeLimitTests(SmallPackageFactory factory)
    {
        _factory = factory;
    }

    public sealed class SmallPackageFactory : PlatformApiFactory
    {
        protected override void ConfigureTestSettings(IDictionary<string, string?> settings) =>
            settings["Versions:MaxPackageSizeBytes"] = "1024";
    }

    [Fact]
    public async Task 超过包上限的登记返回221而票据仍可按较大额度放行()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        // 票据按 max(MaxFileSizeBytes, MaxPackageSizeBytes) 放行，所以这一步能成功。
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, payload.Length))));

        using (var upload = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(payload),
        })
        {
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            _ = await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(upload));
        }

        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                sha,
                payload.Length,
                "Setup-size-1.0.0.exe",
                VersionConventions.PackageKindFull,
                Target: "any",
                Format: "exe")));

        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageTooLarge,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.RequestEntityTooLarge));
    }
}

/// <summary>版本入口限流按用户 + 应用分区，超过配额返回 429。</summary>
public sealed class VersionsUploadRateLimitTests : IClassFixture<VersionsUploadRateLimitTests.TightLimitFactory>
{
    private readonly TightLimitFactory _factory;

    public VersionsUploadRateLimitTests(TightLimitFactory factory)
    {
        _factory = factory;
    }

    public sealed class TightLimitFactory : PlatformApiFactory
    {
        protected override void ConfigureTestSettings(IDictionary<string, string?> settings) =>
            settings["RateLimits:VersionsUploadTicketPerMinute"] = "2";
    }

    [Fact]
    public async Task 票据申请超过每分钟配额返回429()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var payload = "rate-limit"u8.ToArray();

        foreach (var sha in new[] { new string('a', 64), new string('b', 64) })
        {
            _ = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
                await client.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/versions/uploads",
                    token,
                    new CreateUploadTicketRequest(sha, payload.Length))));
        }

        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/uploads",
            token,
            new CreateUploadTicketRequest(new string('c', 64), payload.Length)));

        Assert.Equal(
            (int)ApiErrorCode.RateLimited,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.TooManyRequests));
    }
}
