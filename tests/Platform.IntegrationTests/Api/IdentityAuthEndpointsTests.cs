using System.Net;
using System.Net.Http.Json;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 桌面端 body-refresh 与管理端 cookie-refresh 两条登录链路，
/// 覆盖轮换、复用检测、登出幂等与自助改密后的会话吊销。
/// </summary>
public sealed class IdentityAuthEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public IdentityAuthEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 桌面登录返回主体信息且访问令牌可用于主体自述()
    {
        using var client = _factory.CreateClient();

        var session = await IdentityTestHelpers.LoginAsync(client, "ADMIN", IdentityTestHelpers.AdminPassword);

        Assert.StartsWith(IdentityConventions.RefreshTokenPrefix, session.RefreshToken, StringComparison.Ordinal);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(IdentityTestHelpers.AdminUserName, session.User.UserName);
        Assert.True(session.User.MustChangePassword);
        Assert.Contains(IdentityConventions.PlatformAdminRole, session.User.Roles);
        Assert.Equal(new[] { PermissionCatalog.Wildcard }, session.User.Permissions);
        Assert.Contains(IdentityConventions.DemoAppKey, session.User.AppKeys);

        using var whoami = IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", session.AccessToken);
        var principal = await IdentityTestHelpers.ReadDataAsync<WhoAmIResponse>(
            await client.SendAsync(whoami));

        Assert.True(principal.IsAuthenticated);
        Assert.False(principal.IsService);
        Assert.Equal(IdentityTestHelpers.AdminUserName, principal.UserName);
        Assert.Single(principal.Roles);
        Assert.Contains(IdentityConventions.DemoAppKey, principal.AppKeys);
        // 未显式指定应用时，令牌的"当前应用"取可访问列表首个（种子只有 demo）
        Assert.Equal(IdentityConventions.DemoAppKey, principal.AppKey);
    }

    [Fact]
    public async Task 错误口令与未知用户统一返回无效凭据()
    {
        using var client = _factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest(IdentityTestHelpers.AdminUserName, "Wrong@12345"));
        Assert.Equal(
            (int)ApiErrorCode.IdentityInvalidCredentials,
            await IdentityTestHelpers.ReadErrorCodeAsync(wrongPassword, HttpStatusCode.Unauthorized));

        var unknownUser = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest("ghost-user", "Wrong@12345"));
        Assert.Equal(
            (int)ApiErrorCode.IdentityInvalidCredentials,
            await IdentityTestHelpers.ReadErrorCodeAsync(unknownUser, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 刷新轮换令牌且旧令牌重放会吊销整个家族()
    {
        using var client = _factory.CreateClient();
        var login = await IdentityTestHelpers.LoginAsync(
            client, IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword);

        var rotated = await IdentityTestHelpers.ReadDataAsync<RefreshTokenResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/identity/refresh",
                new RefreshTokenRequest(login.RefreshToken)));

        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.Equal(login.User.UserId, rotated.User.UserId);

        // 旧令牌再次出现 = 泄露信号：重放请求本身失败
        var replay = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(replay, HttpStatusCode.Unauthorized));

        // 合法持有人手里的新令牌也被连坐吊销，必须重新登录
        var successor = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(rotated.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(successor, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 登出幂等且刷新令牌立即失效()
    {
        using var client = _factory.CreateClient();
        var login = await IdentityTestHelpers.LoginAsync(
            client, IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword);

        var logout = await client.PostAsJsonAsync(
            "/api/v1/identity/logout",
            new LogoutRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var logoutAgain = await client.PostAsJsonAsync(
            "/api/v1/identity/logout",
            new LogoutRequest());
        Assert.Equal(HttpStatusCode.OK, logoutAgain.StatusCode);

        var refresh = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(refresh, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 管理端刷新令牌只走HttpOnly严格SameSite的Cookie()
    {
        using var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/auth/login",
            new LoginRequest(IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword));
        var login = await IdentityTestHelpers.ReadDataAsync<AdminSessionResponse>(loginResponse);

        var loginCookie = ReadSetCookie(loginResponse);
        Assert.Contains("httponly", loginCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", loginCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={IdentityConventions.RefreshCookiePath}", loginCookie, StringComparison.OrdinalIgnoreCase);

        // 旧名必须顺手清掉：HTTPS 期写入的 Secure 旧 Cookie 在 HTTP 下无法被同名非 Secure Cookie 覆盖，
        // 继续读旧名会让「HTTPS 切 HTTP」后的每次刷新都 401。
        var legacy = Assert.Single(
            loginResponse.Headers.GetValues("Set-Cookie"),
            x => x.StartsWith($"{IdentityConventions.LegacyRefreshCookieName}=", StringComparison.Ordinal));
        Assert.Contains("expires=thu, 01 jan 1970", legacy, StringComparison.OrdinalIgnoreCase);

        var refreshResponse = await client.PostAsync("/api/v1/admin/auth/refresh", content: null);
        var refreshed = await IdentityTestHelpers.ReadDataAsync<AdminSessionResponse>(refreshResponse);
        Assert.NotEqual(login.AccessToken, refreshed.AccessToken);
        Assert.NotEqual(ReadSetCookie(loginResponse), ReadSetCookie(refreshResponse));

        var logout = await client.PostAsync("/api/v1/admin/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains(
            "expires=thu, 01 jan 1970",
            ReadSetCookie(logout),
            StringComparison.OrdinalIgnoreCase);

        // Cookie 已删除：刷新请求拿不到令牌，按 113 处理而不是 401 以外的状态
        var afterLogout = await client.PostAsync("/api/v1/admin/auth/refresh", content: null);
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(afterLogout, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task 刷新兼容旧Cookie名并换成新名()
    {
        using var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/auth/login",
            new LoginRequest(IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword));
        var token = ReadCookieValue(ReadSetCookie(loginResponse));

        // 只带旧名（例如 HTTPS 升级上来的现场）：仍可续期一次
        using var legacyClient = _factory.CreateClient();
        using var legacyRefresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/auth/refresh");
        legacyRefresh.Headers.Add(
            "Cookie",
            $"{IdentityConventions.LegacyRefreshCookieName}={token}");
        var response = await legacyClient.SendAsync(legacyRefresh);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(token, ReadCookieValue(ReadSetCookie(response)));
    }

    [Fact]
    public async Task 自助改密后旧刷新令牌失效且新口令可登录()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        const string userName = "pw-tester";
        const string newPassword = "Tester@54321";

        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, userName, IdentityTestHelpers.AdminPassword, "改密测试账号");

        var login = await IdentityTestHelpers.LoginAsync(client, userName, IdentityTestHelpers.AdminPassword);
        Assert.True(login.User.MustChangePassword);

        var change = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/identity/password/change",
            login.AccessToken,
            new ChangePasswordRequest(IdentityTestHelpers.AdminPassword, newPassword)));
        var changed = await IdentityTestHelpers.ReadDataAsync<PasswordChangedResponse>(change);
        Assert.True(changed.RevokedSessions >= 1);

        var refresh = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(refresh, HttpStatusCode.Unauthorized));

        var oldPasswordLogin = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest(userName, IdentityTestHelpers.AdminPassword));
        Assert.Equal(
            (int)ApiErrorCode.IdentityInvalidCredentials,
            await IdentityTestHelpers.ReadErrorCodeAsync(oldPasswordLogin, HttpStatusCode.Unauthorized));

        var newLogin = await IdentityTestHelpers.LoginAsync(client, userName, newPassword);
        Assert.False(newLogin.User.MustChangePassword);
    }

    private static string ReadSetCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        return values.Single(x => x.StartsWith(IdentityConventions.RefreshCookieName, StringComparison.Ordinal));
    }

    private static string ReadCookieValue(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0];
        return pair[(pair.IndexOf('=') + 1)..];
    }
}
