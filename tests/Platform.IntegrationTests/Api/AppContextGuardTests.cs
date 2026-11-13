using System.Net;
using System.Net.Http.Json;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 停用应用拦 JWT（B7.5-2）：已签发的访问令牌在应用停用后立即失去访问能力，
/// 平台管理员（通配权限）豁免，否则停用后无人能从管理端恢复。
/// </summary>
public sealed class AppContextGuardTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public AppContextGuardTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 停用应用后旧令牌被拒且管理员可重新启用()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"guard-{suffix}";

        var app = await CreateAppAsync(client, admin, appKey);
        var role = await CreateRoleAsync(client, admin, $"guard-role-{suffix}", appKey);
        var userName = $"guard-user-{suffix}";
        const string password = "Guard@12345";
        await IdentityTestHelpers.CreateUserAsync(client, admin, userName, password, roleIds: [role.Id]);

        var login = await IdentityTestHelpers.LoginAsync(client, userName, password, appKey);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, login.AccessToken));

        // 绑定到同一应用的平台管理员令牌：停用后它必须仍然可用，否则没人能把应用启用回来
        var guardAdmin = await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/dev/token",
                new DevTokenRequest(
                    Subject: "guard-admin",
                    Name: "守卫测试管理员",
                    Permissions: [PermissionCatalog.Wildcard],
                    Apps: [appKey],
                    AppKey: appKey)));

        await SetEnabledAsync(client, guardAdmin.AccessToken, app.Id, enabled: false);

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", login.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.AppsDisabled,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Forbidden));

        // 刷新同样拒绝：会话工厂发现当前应用已停用
        var refresh = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(refresh, HttpStatusCode.Forbidden));

        // 管理员令牌不被停用影响，可以把应用重新启用；旧访问令牌随即恢复
        await SetEnabledAsync(client, guardAdmin.AccessToken, app.Id, enabled: true);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(client, login.AccessToken));
    }

    private static async Task<HttpStatusCode> WhoAmIStatusAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", token));
        return response.StatusCode;
    }

    private static async Task SetEnabledAsync(HttpClient client, string token, Guid appId, bool enabled)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/apps/{appId}",
            token,
            new UpdateAppRequest(IsEnabled: enabled)));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<AppDetailDto> CreateAppAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                token,
                new CreateAppRequest(key, key))));

    private static async Task<RoleSummaryDto> CreateRoleAsync(
        HttpClient client,
        string token,
        string name,
        string appKey) =>
        await IdentityTestHelpers.ReadDataAsync<RoleSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/identity/roles",
                token,
                new CreateRoleRequest(
                    name,
                    DisplayName: name,
                    Scope: IdentityConventions.ScopeApp,
                    AppKey: appKey,
                    Permissions: [PermissionCatalog.AppsRead]))));
}
