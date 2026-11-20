using System.Net;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>宽限期设置端点：读写、默认来源标记、校验与权限门禁。</summary>
public sealed class RetentionSettingsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public RetentionSettingsEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 初始回落出厂默认且写入后标记为库内配置()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var initial = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/retention/settings", token)));
        Assert.False(initial.Backups.IsDatabaseValue);
        Assert.Equal(1, initial.Backups.GraceMinutes);

        var updated = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Put,
                "/api/v1/admin/retention/settings",
                token,
                new UpdateRetentionSettingsRequest(45, 90))));
        Assert.True(updated.Backups.IsDatabaseValue);
        Assert.Equal(45, updated.Backups.GraceMinutes);
        Assert.Equal(90, updated.Documents.GraceMinutes);

        var reread = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/retention/settings", token)));
        Assert.Equal(45, reread.Backups.GraceMinutes);
        Assert.Equal(90, reread.Documents.GraceMinutes);
    }

    [Fact]
    public async Task 小于一分钟被拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Put,
            "/api/v1/admin/retention/settings",
            token,
            new UpdateRetentionSettingsRequest(0, 30)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 无权限的用户读不到也改不了()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewerRole = await IdentityTestHelpers.GetRoleAsync(
            client, adminToken, IdentityConventions.ViewerRole);
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "recycle-viewer", "Viewer@12345", roleIds: [viewerRole.Id]);

        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "recycle-viewer", "Viewer@12345")).AccessToken;

        var read = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/retention/settings", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(read, HttpStatusCode.Forbidden));

        var write = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Put,
            "/api/v1/admin/retention/settings",
            viewerToken,
            new UpdateRetentionSettingsRequest(30, 30)));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(write, HttpStatusCode.Forbidden));
    }
}
