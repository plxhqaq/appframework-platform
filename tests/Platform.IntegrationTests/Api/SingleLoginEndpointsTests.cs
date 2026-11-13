using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Services;

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
        Assert.StartsWith("http", created.Payload, StringComparison.Ordinal);
        Assert.EndsWith(
            QrLoginConventions.ApprovalPath + created.RequestId.ToString("N"),
            created.Payload,
            StringComparison.Ordinal);

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

    [Fact]
    public async Task 现场工程师账号能签发密钥也能审批扫码()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var engineer = await LoginAsEngineerAsync(client, admin, "field-engineer");

        var issued = await IssueKeyAsync(client, engineer, durationMinutes: 120);
        Assert.Equal(120, issued.DurationMinutes);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "产线-PC-07")));

        var detail = await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/identity/qr/sessions/{created.RequestId}",
                engineer)));
        Assert.Equal(QrLoginStatuses.Pending, detail.Status);
        Assert.Equal("产线-PC-07", detail.ClientName);

        var approved = await ApproveAsync(client, engineer, created.RequestId);
        Assert.Equal(QrLoginStatuses.Approved, approved.Status);

        var listed = await IdentityTestHelpers.ReadDataAsync<PageResult<QrSessionSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/identity/qr/sessions?status=approved",
                engineer)));
        Assert.Contains(listed.Items, x => x.RequestId == created.RequestId);
    }

    [Fact]
    public async Task 临时会话不能签发密钥也不能审批扫码()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var issued = await IssueKeyAsync(client, admin);
        var redeemed = await RedeemAsync(client, issued.Key);

        // 正常签发的临时令牌已经被摘掉入口权限，这里再单独造一个"权限齐全但仍是临时身份"的令牌，
        // 验证挡它的是身份种类而不是权限集合——管理员把角色改开了也续不了期。
        var granted = await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/dev/token",
                new DevTokenRequest(
                    Roles: [SingleLoginConventions.EngineerRole],
                    Permissions: [PermissionCatalog.IdentitySingleKeyIssue, PermissionCatalog.IdentityQrApprove],
                    Temporary: true)));

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "自我续期尝试")));

        foreach (var token in new[] { redeemed.AccessToken, granted.AccessToken })
        {
            var issue = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/identity/singlekeys", token, new IssueSingleLoginKeyRequest()));
            Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);

            var list = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/identity/qr/sessions", token));
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

            var detail = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/identity/qr/sessions/{created.RequestId}", token));
            Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);

            var approve = await ApproveRawAsync(client, token, created.RequestId);
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

            var reject = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/identity/qr/sessions/{created.RequestId}/reject", token));
            Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
        }

        // 拦下来之后会话仍是待审批，真人账号还能正常处理。
        var stillPending = await GetQrStatusAsync(client, created.RequestId);
        Assert.Equal(QrLoginStatuses.Pending, stillPending.Status);
        Assert.Equal(QrLoginStatuses.Approved, (await ApproveAsync(client, admin, created.RequestId)).Status);
    }

    [Fact]
    public async Task 有效期可设到两小时且超出按上限钳制()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        Assert.Equal(120, SingleLoginConventions.MaxDurationMinutes);
        Assert.Equal(120, QrLoginConventions.MaxDurationMinutes);

        var twoHours = await IssueKeyAsync(client, admin, durationMinutes: 120);
        Assert.Equal(120, twoHours.DurationMinutes);
        Assert.True(twoHours.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));

        var clamped = await IssueKeyAsync(client, admin, durationMinutes: 600);
        Assert.Equal(120, clamped.DurationMinutes);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "长班次客户端")));
        await ApproveAsync(client, admin, created.RequestId, durationMinutes: 600);

        var status = await GetQrStatusAsync(client, created.RequestId);
        Assert.NotNull(status.Session);
        Assert.True(status.Session!.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));
        Assert.True(status.Session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(121));
    }

    [Fact]
    public async Task 扫码载荷是审批页链接且优先取配置基址()
    {
        using var configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["QrLogin:ApprovalUrlBase"] = "https://ops.example.com:8444/",
                })));
        using var client = configured.CreateClient();

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(QrCreatePath, new CreateQrSessionRequest("qr-device", "手机扫码")));

        // 反代会把 Host 改成不带端口的主机名，所以配置的基址必须赢过请求推导，末尾斜杠也不能重复。
        Assert.Equal(
            $"https://ops.example.com:8444{QrLoginConventions.ApprovalPath}{created.RequestId:N}",
            created.Payload);
    }

    [Fact]
    public async Task 老库工程师角色再次种子后补齐入口权限且不冲掉手工权限()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var role = await db.Set<Role>().SingleAsync(x => x.Name == SingleLoginConventions.EngineerRole);
            role.Permissions = [PermissionCatalog.BackupRead, "custom.manual"];
            role.DisplayName = "工程师（临时登录）";
            await db.SaveChangesAsync();
        }

        await _factory.Services.GetRequiredService<IdentitySeeder>().StartAsync(CancellationToken.None);

        var upgraded = await IdentityTestHelpers.GetRoleAsync(client, admin, SingleLoginConventions.EngineerRole);
        Assert.Contains(PermissionCatalog.IdentitySingleKeyIssue, upgraded.Permissions);
        Assert.Contains(PermissionCatalog.IdentityQrApprove, upgraded.Permissions);
        Assert.Contains("custom.manual", upgraded.Permissions);
        Assert.Equal("现场工程师", upgraded.DisplayName);
    }

    private static async Task<string> LoginAsEngineerAsync(HttpClient client, string admin, string userName)
    {
        var role = await IdentityTestHelpers.GetRoleAsync(client, admin, SingleLoginConventions.EngineerRole);
        await IdentityTestHelpers.CreateUserAsync(
            client,
            admin,
            userName,
            "Engineer@12345",
            "现场工程师",
            roleIds: [role.Id]);
        return (await IdentityTestHelpers.LoginAsync(client, userName, "Engineer@12345")).AccessToken;
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

    private static async Task<QrSessionSummaryDto> ApproveAsync(
        HttpClient client,
        string token,
        Guid requestId,
        int? durationMinutes = null)
    {
        var response = await ApproveRawAsync(client, token, requestId, durationMinutes);
        return await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(response);
    }

    private static Task<HttpResponseMessage> ApproveRawAsync(
        HttpClient client,
        string token,
        Guid requestId,
        int? durationMinutes = null) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/identity/qr/sessions/{requestId}/approve",
            token,
            new ApproveQrSessionRequest(durationMinutes)));

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
