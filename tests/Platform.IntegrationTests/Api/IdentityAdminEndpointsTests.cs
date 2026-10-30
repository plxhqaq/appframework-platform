using System.Net;
using System.Net.Http.Json;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 管理端身份管理：用户/角色 CRUD、权限码校验、角色在用保护，
/// 以及"改角色权限 → 刷新令牌后立即生效"这条 B2 验收主线。
/// </summary>
public sealed class IdentityAdminEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public IdentityAdminEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 用户创建可幂等重放且重复用户名冲突()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await IdentityTestHelpers.CreateUserAsync(
            client, token, "crud-user", "Crud@12345", "CRUD 用户", idempotencyKey: "create-crud-user");
        var replayed = await IdentityTestHelpers.CreateUserAsync(
            client, token, "crud-user", "Crud@12345", "CRUD 用户", idempotencyKey: "create-crud-user");
        Assert.Equal(created.Id, replayed.Id);

        var duplicate = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/users",
            token,
            new CreateUserRequest("crud-user", "Crud@12345")));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(duplicate, HttpStatusCode.Conflict));

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<UserSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/identity/users?keyword=crud", token)));
        Assert.Contains(page.Items, x => x.Id == created.Id);
    }

    [Fact]
    public async Task 更新用户角色与禁用后登录被拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewer = await IdentityTestHelpers.GetRoleAsync(client, token, IdentityConventions.ViewerRole);

        var created = await IdentityTestHelpers.CreateUserAsync(
            client, token, "role-user", "Role@12345", "角色测试");

        var update = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/identity/users/{created.Id}",
            token,
            new UpdateUserRequest(DisplayName: "已改名", RoleIds: [viewer.Id])));
        var updated = await IdentityTestHelpers.ReadDataAsync<UserDetailDto>(update);

        Assert.Equal("已改名", updated.DisplayName);
        Assert.Equal(new[] { viewer.Id }, updated.RoleIds);
        Assert.Contains(updated.Roles, x => string.Equals(x.Name, IdentityConventions.ViewerRole, StringComparison.Ordinal));

        var disable = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/identity/users/{created.Id}",
            token,
            new UpdateUserRequest(IsEnabled: false)));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var login = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest("role-user", "Role@12345"));
        Assert.Equal(
            (int)ApiErrorCode.IdentityUserDisabled,
            await IdentityTestHelpers.ReadErrorCodeAsync(login, HttpStatusCode.Forbidden));

        var enable = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/identity/users/{created.Id}",
            token,
            new UpdateUserRequest(IsEnabled: true)));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var delete = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/identity/users/{created.Id}", token));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/identity/users/{created.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.IdentityUserNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 角色创建校验权限码并保护内置角色()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await CreateRoleAsync(
            client, token, "ops-reader", [PermissionCatalog.VersionsChannelRead]);
        Assert.False(created.IsSystem);
        Assert.Equal(IdentityConventions.ScopePlatform, created.Scope);

        var unknown = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/roles",
            token,
            new CreateRoleRequest("bad-code", Permissions: ["does.not.exist"])));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionUnknown,
            await IdentityTestHelpers.ReadErrorCodeAsync(unknown, HttpStatusCode.BadRequest));

        var wildcard = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/roles",
            token,
            new CreateRoleRequest("wildcard-role", Permissions: [PermissionCatalog.Wildcard])));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(wildcard, HttpStatusCode.Forbidden));

        var viewer = await IdentityTestHelpers.GetRoleAsync(client, token, IdentityConventions.ViewerRole);
        var deleteSystemRole = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/identity/roles/{viewer.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(deleteSystemRole, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 在用角色不可删除且移除用户后可删除()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var role = await CreateRoleAsync(client, token, "in-use-role", [PermissionCatalog.VersionsReleaseRead]);
        var user = await IdentityTestHelpers.CreateUserAsync(
            client, token, "in-use-user", "InUse@12345", roleIds: [role.Id]);

        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/identity/roles/{role.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRoleInUse,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Conflict));

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/identity/users/{user.Id}", token))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/identity/roles/{role.Id}", token))).StatusCode);
    }

    [Fact]
    public async Task 角色权限变更在刷新令牌后立即生效()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var role = await CreateRoleAsync(client, token, "perm-roll", [PermissionCatalog.VersionsChannelRead]);
        await IdentityTestHelpers.CreateUserAsync(
            client, token, "perm-user", "Perm@12345", roleIds: [role.Id]);

        var login = await IdentityTestHelpers.LoginAsync(client, "perm-user", "Perm@12345");
        Assert.Equal(new[] { role.Name }, login.User.Roles);
        Assert.Equal(new[] { PermissionCatalog.VersionsChannelRead }, login.User.Permissions);

        // 没有 identity.user.read：鉴权层统一返回 403
        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/identity/users", login.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));

        var update = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/identity/roles/{role.Id}",
            token,
            new UpdateRoleRequest(Permissions:
            [
                PermissionCatalog.VersionsChannelRead,
                PermissionCatalog.IdentityUserRead,
            ])));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // 访问令牌仍带着旧权限；刷新后会话按库重建，新权限立即可用
        var refreshed = await IdentityTestHelpers.ReadDataAsync<RefreshTokenResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/identity/refresh",
                new RefreshTokenRequest(login.RefreshToken)));
        Assert.Contains(PermissionCatalog.IdentityUserRead, refreshed.User.Permissions);

        var allowed = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/identity/users", refreshed.AccessToken));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task 会话列表可见且吊销后刷新失败()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var user = await IdentityTestHelpers.CreateUserAsync(
            client, token, "session-user", "Session@12345");

        var login = await IdentityTestHelpers.LoginAsync(client, "session-user", "Session@12345");

        var sessions = await IdentityTestHelpers.ReadDataAsync<List<SessionSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/identity/users/{user.Id}/sessions", token)));
        var session = Assert.Single(sessions);
        Assert.True(session.IsActive);
        Assert.Equal(user.Id, session.UserId);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<SessionSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/identity/sessions?userId={user.Id}", token)));
        Assert.Equal(1L, page.Total);

        var revoke = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/identity/sessions/{session.Id}/revoke", token));
        var revoked = await IdentityTestHelpers.ReadDataAsync<SessionSummaryDto>(revoke);
        Assert.False(revoked.IsActive);

        var refresh = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(refresh, HttpStatusCode.Unauthorized));
    }

    private static async Task<RoleSummaryDto> CreateRoleAsync(
        HttpClient client,
        string token,
        string name,
        IReadOnlyList<string> permissions) =>
        await IdentityTestHelpers.ReadDataAsync<RoleSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/identity/roles",
                token,
                new CreateRoleRequest(name, DisplayName: name, Permissions: permissions))));
}
