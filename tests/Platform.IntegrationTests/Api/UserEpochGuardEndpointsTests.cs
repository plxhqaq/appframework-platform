using System.Net;
using System.Net.Http.Json;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// D7 用户级 epoch 守卫：吊销/改密/登出 bump users.token_epoch 后，旧访问令牌在所有路径
/// （含 /context 换发、QR 审批）立即 401（测试宿主缓存窗口为 0），新登录不受影响；
/// service/dev 令牌豁免。
/// </summary>
public sealed class UserEpochGuardEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public UserEpochGuardEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 单会话吊销后旧令牌连context换发都被封死()
    {
        // 两个独立客户端：吊销动作不能覆盖被测试客户端的刷新 Cookie，
        // 以证明 /context 被封死来自 epoch 守卫而不是 Cookie 失效。
        using var victim = _factory.CreateClient();
        using var revoker = _factory.CreateClient();

        var fingerprint = $"epoch-single-{Guid.NewGuid():N}";
        var session = await IdentityTestHelpers.ReadDataAsync<AdminSessionResponse>(
            await victim.PostAsJsonAsync(
                "/api/v1/admin/auth/login",
                new LoginRequest(
                    IdentityTestHelpers.AdminUserName,
                    IdentityTestHelpers.AdminPassword,
                    DeviceFingerprint: fingerprint)));
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(revoker);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(victim, session.AccessToken));

        var live = Assert.Single(
            (await ListSessionsAsync(revoker, adminToken, session.User.UserId)).Items,
            x => x.IsActive && x.DeviceFingerprint == fingerprint);

        await IdentityTestHelpers.ReadDataAsync<SessionSummaryDto>(await revoker.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/sessions/{live.Id}/revoke",
                adminToken)));

        // 旧访问令牌：普通路径 401
        var blocked = await victim.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", session.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));

        // D7 的关键点：/context 走刷新 Cookie 自续期，也必须被 epoch 守卫封死
        var contextSwitch = await victim.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/auth/context",
            session.AccessToken,
            new SwitchAppContextRequest(IdentityConventions.DemoAppKey)));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(contextSwitch, HttpStatusCode.Unauthorized));

        // 旧令牌永久失效，新登录立即恢复
        var renewed = await IdentityTestHelpers.ReadDataAsync<AdminSessionResponse>(
            await victim.PostAsJsonAsync(
                "/api/v1/admin/auth/login",
                new LoginRequest(
                    IdentityTestHelpers.AdminUserName,
                    IdentityTestHelpers.AdminPassword,
                    DeviceFingerprint: fingerprint)));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(victim, renewed.AccessToken));
    }

    [Fact]
    public async Task 全量吊销后该用户全部会话立即401且重新登录恢复()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"epoch-multi-{suffix}";
        var user = await IdentityTestHelpers.CreateUserAsync(client, admin, userName, "Epoch@12345");

        var first = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");
        var second = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, first.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, second.AccessToken));

        await RevokeAllAsync(client, admin, user.Id);

        // 两条访问令牌都不依赖刷新是否轮换：epoch 一 bump 全部立即失效
        foreach (var accessToken in new[] { first.AccessToken, second.AccessToken })
        {
            var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/platform/whoami", accessToken));
            Assert.Equal(
                (int)ApiErrorCode.IdentityRefreshTokenRevoked,
                await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));
        }

        var relogin = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, relogin.AccessToken));
    }

    [Fact]
    public async Task 管理端重置口令触发epoch吊销全部旧会话()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"epoch-reset-{suffix}";
        var user = await IdentityTestHelpers.CreateUserAsync(client, admin, userName, "Epoch@12345");

        var login = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");

        await IdentityTestHelpers.ReadDataAsync<RevokedSessionsResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/users/{user.Id}/password/reset",
                admin,
                new ResetUserPasswordRequest("Epoch@67890", RequireChange: false))));

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", login.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));

        var relogin = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@67890");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, relogin.AccessToken));
    }

    [Fact]
    public async Task 自助改密同样吊销旧访问令牌()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"epoch-self-{suffix}";
        await IdentityTestHelpers.CreateUserAsync(client, admin, userName, "Epoch@12345");

        var login = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");

        var changed = await IdentityTestHelpers.ReadDataAsync<PasswordChangedResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/identity/password/change",
                login.AccessToken,
                new ChangePasswordRequest("Epoch@12345", "Epoch@67890"))));
        Assert.True(changed.RevokedSessions >= 1);

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", login.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));

        var relogin = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@67890");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, relogin.AccessToken));
    }

    [Fact]
    public async Task 登出也是用户级吊销同用户其它会话一并失效()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"epoch-logout-{suffix}";
        await IdentityTestHelpers.CreateUserAsync(client, admin, userName, "Epoch@12345");

        var sessionA = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");
        var sessionB = await IdentityTestHelpers.LoginAsync(client, userName, "Epoch@12345");

        var logout = await client.PostAsJsonAsync(
            "/api/v1/identity/logout",
            new LogoutRequest(sessionA.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // 语义点：logout 走吊销收口路径 → bump 用户级 epoch，该用户全部访问令牌失效，
        // 不只是本次登出的那条会话（sessionB 同样被踢）。
        foreach (var accessToken in new[] { sessionA.AccessToken, sessionB.AccessToken })
        {
            var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/platform/whoami", accessToken));
            Assert.Equal(
                (int)ApiErrorCode.IdentityRefreshTokenRevoked,
                await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task 吊销后旧令牌发起QR审批被epoch守卫封死()
    {
        using var client = _factory.CreateClient();
        var staleAdmin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(
                "/api/v1/identity/qr/sessions",
                new CreateQrSessionRequest(
                    AppKey: IdentityConventions.DemoAppKey,
                    DeviceFingerprint: $"epoch-qr-{Guid.NewGuid():N}",
                    ClientName: "守卫-被封死的审批人")));

        var adminId = Guid.Parse((await ReadWhoAmIAsync(client, staleAdmin)).UserId!);
        var freshAdmin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await RevokeAllAsync(client, freshAdmin, adminId);

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/identity/qr/sessions/{created.RequestId}/approve",
            staleAdmin,
            new ApproveQrSessionRequest()));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));

        // 审批本身仍可完成：重新登录的管理员批准同一单成功
        var recoveryAdmin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var approved = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/identity/qr/sessions/{created.RequestId}/approve",
            recoveryAdmin,
            new ApproveQrSessionRequest()));
        Assert.NotNull(await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(approved));
    }

    [Fact]
    public async Task service与dev令牌不受用户epoch吊销影响()
    {
        using var client = _factory.CreateClient();
        var dev = await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/dev/token",
                new DevTokenRequest(Subject: $"epoch-exempt-{Guid.NewGuid():N}")));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, dev.AccessToken));

        // 全量吊销管理员（含 dev 令牌签发方无关的用户）后：user 令牌 401、service/dev 令牌照常。
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var adminId = (await ReadWhoAmIAsync(client, admin)).UserId!;
        await RevokeAllAsync(client, admin, Guid.Parse(adminId));

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", admin));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, dev.AccessToken));
    }

    private static async Task<HttpStatusCode> WhoAmIStatusAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", token));
        return response.StatusCode;
    }

    private static async Task<WhoAmIResponse> ReadWhoAmIAsync(HttpClient client, string token) =>
        await IdentityTestHelpers.ReadDataAsync<WhoAmIResponse>(await client.SendAsync(
            IdentityTestHelpers.Authorized(HttpMethod.Get, "/api/v1/platform/whoami", token)));

    private static async Task<PageResult<SessionSummaryDto>> ListSessionsAsync(
        HttpClient client,
        string adminToken,
        Guid userId) =>
        await IdentityTestHelpers.ReadDataAsync<PageResult<SessionSummaryDto>>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/identity/sessions?userId={userId:D}",
                adminToken)));

    private static async Task RevokeAllAsync(HttpClient client, string adminToken, Guid userId) =>
        await IdentityTestHelpers.ReadDataAsync<RevokedSessionsResponse>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/users/{userId:D}/sessions/revoke",
                adminToken)));

    /// <summary>全量吊销/重置口令端点的计数响应体。</summary>
    private sealed record RevokedSessionsResponse(int RevokedSessions);
}
