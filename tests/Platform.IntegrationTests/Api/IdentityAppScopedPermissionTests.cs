using System.Net;
using System.Net.Http.Json;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 应用级角色只在该应用上下文授予权限（B7.5-1）：
/// 同一用户在不同应用下拿到不同权限集合，平台角色始终生效，已停用应用不能签发令牌。
/// </summary>
public sealed class IdentityAppScopedPermissionTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public IdentityAppScopedPermissionTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 应用角色权限只按当前应用并入令牌()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"scope-a-{suffix}";
        var appB = $"scope-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var platformRole = await CreateRoleAsync(
            client,
            admin,
            $"platform-{suffix}",
            IdentityConventions.ScopePlatform,
            appKey: null,
            [PermissionCatalog.AppsRead]);
        var roleA = await CreateRoleAsync(
            client,
            admin,
            $"role-a-{suffix}",
            IdentityConventions.ScopeApp,
            appA,
            [PermissionCatalog.VersionsChannelRead, PermissionCatalog.VersionsReleaseRead]);
        var roleB = await CreateRoleAsync(
            client,
            admin,
            $"role-b-{suffix}",
            IdentityConventions.ScopeApp,
            appB,
            [PermissionCatalog.VersionsReleasePublish, PermissionCatalog.VersionsReleaseArchive]);

        var userName = $"scope-user-{suffix}";
        const string password = "Scope@12345";
        await IdentityTestHelpers.CreateUserAsync(
            client,
            admin,
            userName,
            password,
            roleIds: [platformRole.Id, roleA.Id, roleB.Id]);

        var loginA = await IdentityTestHelpers.LoginAsync(client, userName, password, appA);
        Assert.Equal(appA, loginA.User.AppKey);
        Assert.Contains(platformRole.Name, loginA.User.Roles);
        Assert.Contains(roleA.Name, loginA.User.Roles);
        Assert.DoesNotContain(roleB.Name, loginA.User.Roles);
        Assert.Contains(PermissionCatalog.AppsRead, loginA.User.Permissions);
        Assert.Contains(PermissionCatalog.VersionsChannelRead, loginA.User.Permissions);
        Assert.Contains(PermissionCatalog.VersionsReleaseRead, loginA.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.VersionsReleasePublish, loginA.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.VersionsReleaseArchive, loginA.User.Permissions);
        Assert.Contains(appA, loginA.User.AppKeys);
        Assert.Contains(appB, loginA.User.AppKeys);

        var loginB = await IdentityTestHelpers.LoginAsync(client, userName, password, appB);
        Assert.Equal(appB, loginB.User.AppKey);
        Assert.Contains(roleB.Name, loginB.User.Roles);
        Assert.DoesNotContain(roleA.Name, loginB.User.Roles);
        Assert.Contains(PermissionCatalog.VersionsReleasePublish, loginB.User.Permissions);
        Assert.Contains(PermissionCatalog.VersionsReleaseArchive, loginB.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.VersionsChannelRead, loginB.User.Permissions);

        // 未显式指定应用时按应用键排序进入第一个已启用的 app 角色对应应用（A < B）。
        var defaultLogin = await IdentityTestHelpers.LoginAsync(client, userName, password);
        Assert.Equal(appA, defaultLogin.User.AppKey);
        Assert.Contains(PermissionCatalog.VersionsChannelRead, defaultLogin.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.VersionsReleasePublish, defaultLogin.User.Permissions);
    }

    [Fact]
    public async Task 请求已停用应用时不签发令牌()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"scope-off-{suffix}";
        var app = await CreateAppAsync(client, admin, appKey);
        var role = await CreateRoleAsync(
            client,
            admin,
            $"role-off-{suffix}",
            IdentityConventions.ScopeApp,
            appKey,
            [PermissionCatalog.VersionsChannelRead]);

        var userName = $"scope-off-user-{suffix}";
        const string password = "Scope@12345";
        await IdentityTestHelpers.CreateUserAsync(client, admin, userName, password, roleIds: [role.Id]);

        var disable = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/apps/{app.Id}",
            admin,
            new UpdateAppRequest(IsEnabled: false)));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest(userName, password, appKey));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.Forbidden));
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
        string scope,
        string? appKey,
        IReadOnlyList<string> permissions) =>
        await IdentityTestHelpers.ReadDataAsync<RoleSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/identity/roles",
                token,
                new CreateRoleRequest(
                    name,
                    DisplayName: name,
                    Scope: scope,
                    AppKey: appKey,
                    Permissions: permissions))));
}
