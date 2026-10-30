using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Identity.Domain;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 单次密钥与扫码登录：一次性消费、指纹绑定、过期、16 并发只成功一个，
/// 以及扫码会话 pending → approved/rejected 的状态机与临时令牌领取。
/// </summary>
public sealed class SingleLoginEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private const string RedeemPath = "/api/v1/identity/singlekey/redeem";
    private const string QrCreatePath = "/api/v1/identity/qr/sessions";

    private readonly PlatformApiFactory _factory;

    public SingleLoginEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 单次密钥兑换临时令牌且只能使用一次()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var issued = await IssueKeyAsync(client, admin);
        Assert.Matches("^[0-9]{6}$", issued.Key);
        Assert.True(issued.ExpiresAt > DateTimeOffset.UtcNow);

        var redeemed = await RedeemAsync(client, issued.Key);
        Assert.Equal(SingleLoginConventions.TemporaryUserName, redeemed.User.UserName);
        Assert.Equal(new[] { SingleLoginConventions.EngineerRole }, redeemed.User.Roles);
        Assert.True(redeemed.ExpiresAt > DateTimeOffset.UtcNow);

        // 临时身份继承内置工程师角色：能备份下发，但不能维护用户/角色，也不能再签发密钥。
        Assert.Contains(PermissionCatalog.BackupPush, redeemed.User.Permissions);
        Assert.Contains(ClientPermissionCatalog.StationOperate, redeemed.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.IdentityUserManage, redeemed.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.IdentitySingleKeyIssue, redeemed.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.Wildcard, redeemed.User.Permissions);

        var whoami = await IdentityTestHelpers.ReadDataAsync<WhoAmIResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/platform/whoami", redeemed.AccessToken)));
        Assert.True(whoami.IsTemporary);
        Assert.False(whoami.IsService);
        Assert.Equal(SingleLoginConventions.TemporaryUserName, whoami.UserName);

        var replay = await client.PostAsJsonAsync(RedeemPath, new RedeemSingleLoginKeyRequest(issued.Key));
        Assert.Equal(
            (int)ApiErrorCode.IdentitySingleKeyExhausted,
            await IdentityTestHelpers.ReadErrorCodeAsync(replay, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 未知密钥与过期密钥分别返回105与106()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var unknown = await client.PostAsJsonAsync(RedeemPath, new RedeemSingleLoginKeyRequest("000000"));
        Assert.Equal(
            (int)ApiErrorCode.IdentitySingleKeyInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(unknown, HttpStatusCode.Unauthorized));

        var issued = await IssueKeyAsync(client, admin);
        await ExpireSingleKeyAsync(issued.Key);

        var expired = await client.PostAsJsonAsync(RedeemPath, new RedeemSingleLoginKeyRequest(issued.Key));
        Assert.Equal(
            (int)ApiErrorCode.IdentitySingleKeyExpired,
            await IdentityTestHelpers.ReadErrorCodeAsync(expired, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 设备指纹不匹配拒绝且达到尝试上限后作废()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var issued = await IssueKeyAsync(client, admin, fingerprint: "device-a");

        var mismatch = await client.PostAsJsonAsync(
            RedeemPath,
            new RedeemSingleLoginKeyRequest(issued.Key, "device-b"));
        Assert.Equal(
            (int)ApiErrorCode.IdentitySingleKeyDeviceMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(mismatch, HttpStatusCode.Unauthorized));

        // 指纹不匹配只计次数、不消费：正确指纹仍可兑换。
        var redeemed = await RedeemAsync(client, issued.Key, "device-a");
        Assert.Equal(SingleLoginConventions.TemporaryUserName, redeemed.User.UserName);

        var exhaustedKey = await IssueKeyAsync(client, admin, fingerprint: "device-a");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongDevice = await client.PostAsJsonAsync(
                RedeemPath,
                new RedeemSingleLoginKeyRequest(exhaustedKey.Key, "device-b"));
            Assert.Equal(
                (int)ApiErrorCode.IdentitySingleKeyDeviceMismatch,
                await IdentityTestHelpers.ReadErrorCodeAsync(wrongDevice, HttpStatusCode.Unauthorized));
        }

        var blocked = await client.PostAsJsonAsync(
            RedeemPath,
            new RedeemSingleLoginKeyRequest(exhaustedKey.Key, "device-a"));
        Assert.Equal(
            (int)ApiErrorCode.IdentitySingleKeyExhausted,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 十六个并发兑换只有一个成功()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var issued = await IssueKeyAsync(client, admin);

        using var barrier = new SemaphoreSlim(0, 16);
        var tasks = Enumerable.Range(0, 16)
            .Select(async _ =>
            {
                await barrier.WaitAsync();
                return await client.PostAsJsonAsync(
                    RedeemPath,
                    new RedeemSingleLoginKeyRequest(issued.Key));
            })
            .ToArray();

        barrier.Release(16);
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(static response => response.IsSuccessStatusCode));
        foreach (var failure in responses.Where(static response => !response.IsSuccessStatusCode))
        {
            Assert.Equal(
                (int)ApiErrorCode.IdentitySingleKeyExhausted,
                await IdentityTestHelpers.ReadErrorCodeAsync(failure, HttpStatusCode.Unauthorized));
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.Set<SingleLoginKey>().AsNoTracking().SingleAsync(x => x.Key == issued.Key);
        Assert.NotNull(row.UsedAt);
    }

    [Fact]
    public async Task 扫码会话审批通过后客户端可领取临时令牌()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(
                QrCreatePath,
                new CreateQrSessionRequest("qr-device", "产线-PC-01")));
        Assert.StartsWith(QrLoginConventions.PayloadPrefix, created.Payload, StringComparison.Ordinal);
        Assert.Contains(created.RequestId.ToString("N"), created.Payload, StringComparison.Ordinal);

        var pending = await GetQrStatusAsync(client, created.RequestId);
        Assert.Equal(QrLoginStatuses.Pending, pending.Status);
        Assert.Null(pending.Session);

        var approved = await ApproveAsync(client, admin, created.RequestId);
        Assert.Equal(QrLoginStatuses.Approved, approved.Status);
        Assert.Equal(IdentityTestHelpers.AdminUserName, approved.ApprovedByName);

        var status = await GetQrStatusAsync(client, created.RequestId);
        Assert.Equal(QrLoginStatuses.Approved, status.Status);
        Assert.NotNull(status.Session);
        Assert.Equal(SingleLoginConventions.TemporaryUserName, status.Session!.User.UserName);

        var whoami = await IdentityTestHelpers.ReadDataAsync<WhoAmIResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/platform/whoami", status.Session.AccessToken)));
        Assert.True(whoami.IsTemporary);
        Assert.Contains(SingleLoginConventions.EngineerRole, whoami.Roles);

        var secondApprove = await ApproveRawAsync(client, admin, created.RequestId);
        Assert.Equal(
            (int)ApiErrorCode.IdentityQrSessionAlreadyUsed,
            await IdentityTestHelpers.ReadErrorCodeAsync(secondApprove, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 扫码会话拒绝后状态为rejected且不能再审批()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "可疑客户端")));

        var rejected = await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/qr/sessions/{created.RequestId}/reject",
                admin)));
        Assert.Equal(QrLoginStatuses.Rejected, rejected.Status);

        var status = await GetQrStatusAsync(client, created.RequestId);
        Assert.Equal(QrLoginStatuses.Rejected, status.Status);
        Assert.Null(status.Session);

        var approve = await ApproveRawAsync(client, admin, created.RequestId);
        Assert.Equal(
            (int)ApiErrorCode.IdentityQrSessionAlreadyUsed,
            await IdentityTestHelpers.ReadErrorCodeAsync(approve, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 过期扫码会话状态为expired且审批返回110()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "过期客户端")));
        await ExpireQrSessionAsync(created.RequestId);

        var status = await GetQrStatusAsync(client, created.RequestId);
        Assert.Equal(QrLoginStatuses.Expired, status.Status);

        var approve = await ApproveRawAsync(client, admin, created.RequestId);
        Assert.Equal(
            (int)ApiErrorCode.IdentityQrSessionExpired,
            await IdentityTestHelpers.ReadErrorCodeAsync(approve, HttpStatusCode.Gone));
    }

    [Fact]
    public async Task 无权限用户与匿名请求不能签发单次密钥()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewer = await IdentityTestHelpers.GetRoleAsync(client, admin, IdentityConventions.ViewerRole);

        await IdentityTestHelpers.CreateUserAsync(
            client,
            admin,
            "key-viewer",
            "Viewer@12345",
            "密钥只读用户",
            roleIds: [viewer.Id]);
        var viewerLogin = await IdentityTestHelpers.LoginAsync(client, "key-viewer", "Viewer@12345");

        var forbidden = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/singlekeys",
            viewerLogin.AccessToken,
            new IssueSingleLoginKeyRequest()));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var anonymous = await client.PostAsJsonAsync(
            "/api/v1/admin/identity/singlekeys",
            new IssueSingleLoginKeyRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static async Task<SingleLoginKeyDto> IssueKeyAsync(
        HttpClient client,
        string token,
        int? durationMinutes = null,
        string? fingerprint = null)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/singlekeys",
            token,
            new IssueSingleLoginKeyRequest(durationMinutes, fingerprint)));
        return await IdentityTestHelpers.ReadDataAsync<SingleLoginKeyDto>(response);
    }

    private static async Task<TemporarySessionResponse> RedeemAsync(
        HttpClient client,
        string key,
        string? fingerprint = null) =>
        await IdentityTestHelpers.ReadDataAsync<TemporarySessionResponse>(
            await client.PostAsJsonAsync(
                RedeemPath,
                new RedeemSingleLoginKeyRequest(key, fingerprint)));

    private static async Task<QrSessionStatusDto> GetQrStatusAsync(HttpClient client, Guid requestId) =>
        await IdentityTestHelpers.ReadDataAsync<QrSessionStatusDto>(
            await client.GetAsync($"{QrCreatePath}/{requestId}"));

    private static async Task<QrSessionSummaryDto> ApproveAsync(HttpClient client, string token, Guid requestId)
    {
        var response = await ApproveRawAsync(client, token, requestId);
        return await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(response);
    }

    private static Task<HttpResponseMessage> ApproveRawAsync(HttpClient client, string token, Guid requestId) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/identity/qr/sessions/{requestId}/approve",
            token,
            new ApproveQrSessionRequest()));

    private async Task ExpireSingleKeyAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.Set<SingleLoginKey>().SingleAsync(x => x.Key == key);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task ExpireQrSessionAsync(Guid requestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.Set<QrLoginSession>().SingleAsync(x => x.Id == requestId);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
}
