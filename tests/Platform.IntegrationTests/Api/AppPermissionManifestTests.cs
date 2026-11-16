using System.Net;
using System.Net.Http.Json;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 应用权限清单：平台不再硬编码客户端权限，应用自行声明后即可用于角色与 API Key。
/// </summary>
public sealed class AppPermissionManifestTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public AppPermissionManifestTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 清单权限可用于应用角色与密钥范围()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"manifest-a-{suffix}";
        var appB = $"manifest-b-{suffix}";
        var createdAppA = await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var pageCode = $"inspect{suffix}.page.read";
        var panelCode = $"inspect{suffix}.panel.operate";
        await RegisterAsync(client, admin, appA,
            new AppPermissionDto(pageCode, "查看巡检页", "打开并浏览巡检页面", "inspect", "巡检", 10),
            new AppPermissionDto(panelCode, "操作巡检面板", "启停巡检与确认告警", "inspect", "巡检", 20));

        // 合并目录：平台权限 + 该应用声明的权限，说明与分组名来自清单。
        var catalog = await (await client.GetAsync($"/api/v1/platform/permissions?appKey={appA}"))
            .Content.ReadFromJsonAsync<ApiResult<List<PermissionDescriptor>>>();
        var descriptors = catalog!.Data!;
        var page = descriptors.Single(x => x.Code == pageCode);
        Assert.Equal("打开并浏览巡检页面", page.Description);
        Assert.Equal("巡检", page.ModuleLabel);
        Assert.Equal($"app:{appA}:inspect", page.ModuleKey);
        Assert.Contains(descriptors, x => x.Code == PermissionCatalog.VersionsChannelRead);

        // 应用角色可以授予清单权限；另一个应用的角色只拿到平台权限。
        var roleA = await CreateRoleAsync(
            client, admin, $"role-a-{suffix}", IdentityConventions.ScopeApp, appA,
            [pageCode, PermissionCatalog.VersionsChannelRead]);
        var roleB = await CreateRoleAsync(
            client, admin, $"role-b-{suffix}", IdentityConventions.ScopeApp, appB,
            [PermissionCatalog.VersionsReleaseRead]);

        var userName = $"manifest-user-{suffix}";
        const string password = "Manifest@12345";
        await IdentityTestHelpers.CreateUserAsync(
            client, admin, userName, password, roleIds: [roleA.Id, roleB.Id]);

        var loginA = await IdentityTestHelpers.LoginAsync(client, userName, password, appA);
        Assert.Contains(pageCode, loginA.User.Permissions);
        Assert.Contains(PermissionCatalog.VersionsChannelRead, loginA.User.Permissions);
        Assert.DoesNotContain(PermissionCatalog.VersionsReleaseRead, loginA.User.Permissions);
        Assert.True(loginA.User.PermissionsLocked);

        var loginB = await IdentityTestHelpers.LoginAsync(client, userName, password, appB);
        Assert.DoesNotContain(pageCode, loginB.User.Permissions);
        Assert.Contains(PermissionCatalog.VersionsReleaseRead, loginB.User.Permissions);
        Assert.False(loginB.User.PermissionsLocked);

        // API Key 可以授予本应用清单里的权限，但不能授予别的应用的权限。
        var keyRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{createdAppA.Id}/keys",
            admin,
            new CreateApiKeyRequest("巡检", [pageCode]));
        var key = await IdentityTestHelpers.ReadDataAsync<CreatedApiKeyDto>(
            await client.SendAsync(keyRequest));
        Assert.Contains(pageCode, key.Scopes);

        // M2M 连接成功即告知清单锁定状态，客户端据此跳过重复上传。
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apps/token")
        {
            Content = JsonContent.Create(new M2MTokenRequest([pageCode])),
        };
        tokenRequest.Headers.Add(PlatformHeaders.AppKey, appA);
        tokenRequest.Headers.Add(PlatformHeaders.ApiKey, key.Plaintext);
        var m2m = await IdentityTestHelpers.ReadDataAsync<M2MTokenResponse>(
            await client.SendAsync(tokenRequest));
        Assert.True(m2m.PermissionsLocked);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{createdAppA.Id}/keys",
            admin,
            new CreateApiKeyRequest("跨应用", [pageCode, $"other{suffix}.page.read"])));
        Assert.Equal(
            (int)ApiErrorCode.AppsApiKeyScopeUnknown,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 清单拒绝平台权限码与跨应用重复码()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"manifest-conflict-a-{suffix}";
        var appB = $"manifest-conflict-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var platformConflict = await RegisterRawAsync(client, admin, appA, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(PermissionCatalog.VersionsChannelRead, "占位")]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(platformConflict, HttpStatusCode.BadRequest));

        var code = $"shared{suffix}.page.read";
        await RegisterAsync(client, admin, appA, new AppPermissionDto(code, "页面"));

        var crossAppConflict = await RegisterRawAsync(client, admin, appB, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(code, "页面")]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(crossAppConflict, HttpStatusCode.BadRequest));

        // 未声明的自定义权限码仍然不能被角色或密钥使用。
        var unknownRole = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/roles",
            admin,
            new CreateRoleRequest(
                $"unknown-role-{suffix}",
                DisplayName: "未知权限",
                Scope: IdentityConventions.ScopeApp,
                AppKey: appA,
                Permissions: [$"undeclared{suffix}.page.read"])));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionUnknown,
            await IdentityTestHelpers.ReadErrorCodeAsync(unknownRole, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 清单常用组合随权限一起保存并受校验()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"manifest-preset-{suffix}";
        var app = await CreateAppAsync(client, admin, appKey);

        var read = $"preset{suffix}.page.read";
        var operate = $"preset{suffix}.page.operate";
        var manifest = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
                [new AppPermissionDto(read, "查看"), new AppPermissionDto(operate, "操作")],
                [
                    new AppPermissionPresetDto(
                        "operator", "作业员", "只读与基础操作", [read, operate], 10),
                    new AppPermissionPresetDto(
                        "engineer", "工程师", "全部权限", [read, operate], 20),
                ])));

        Assert.Equal(2, manifest.Presets.Count);
        Assert.Equal("operator", manifest.Presets[0].Key);
        Assert.Equal("作业员", manifest.Presets[0].Label);
        Assert.Equal([read, operate], manifest.Presets[0].Permissions);
        Assert.True(manifest.Locked);

        var fetched = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await client.GetAsync($"/api/v1/apps/{appKey}/permissions"));
        Assert.Equal(2, fetched.Presets.Count);
        Assert.Equal(2, fetched.Permissions.Count);
        Assert.True(fetched.Locked);

        // 校验失败不会解锁，因此下一次上传前需要管理端人工解锁。
        await UnlockAsync(client, admin, app.Id);

        // 组合引用未声明的权限码：整表拒绝。
        var undeclared = await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(read, "查看")],
            [new AppPermissionPresetDto("bad", "坏组合", Permissions: [read, $"undeclared{suffix}.x.y"])]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(undeclared, HttpStatusCode.BadRequest));

        // 组合键重复同样拒绝。
        var duplicate = await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(read, "查看")],
            [
                new AppPermissionPresetDto("same", "A", Permissions: [read]),
                new AppPermissionPresetDto("same", "B", Permissions: [read]),
            ]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(duplicate, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 首次上传后清单锁定必须人工解锁才能再次替换()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"manifest-lock-{suffix}";
        var app = await CreateAppAsync(client, admin, appKey);

        var first = $"lock{suffix}.page.read";
        var locked = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
                [new AppPermissionDto(first, "查看")])));
        Assert.True(locked.Locked);
        Assert.Single(locked.Permissions);

        // 锁定后即使新清单与旧清单有交集也不允许上传。
        var blocked = await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(first, "查看"), new AppPermissionDto($"lock{suffix}.page.write", "编辑")]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestLocked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Conflict));

        var fetched = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await client.GetAsync($"/api/v1/apps/{appKey}/permissions"));
        Assert.True(fetched.Locked);

        // 人工解锁后允许整表替换（即使是完全不同的权限），替换成功会重新锁定。
        var unlocked = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await UnlockAsync(client, admin, app.Id));
        Assert.False(unlocked.Locked);

        var replaced = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
                [new AppPermissionDto($"other{suffix}.page.read", "别的应用")],
                [new AppPermissionPresetDto("only", "唯一", Permissions: [$"other{suffix}.page.read"])])));
        Assert.True(replaced.Locked);
        Assert.Single(replaced.Permissions);
        Assert.Equal($"other{suffix}.page.read", replaced.Permissions[0].Code);
        Assert.Single(replaced.Presets);
    }

    [Fact]
    public async Task 手动锁解锁受权限码保护且内置角色已授予()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        // 内置角色：应用管理员与现场工程师都带上新的锁定权限。
        var appAdminRole = await IdentityTestHelpers.GetRoleAsync(
            client, admin, IdentityConventions.AppAdminRole);
        Assert.Contains(PermissionCatalog.AppsPermissionsManage, appAdminRole.Permissions);
        var engineerRole = await IdentityTestHelpers.GetRoleAsync(
            client, admin, SingleLoginConventions.EngineerRole);
        Assert.Contains(PermissionCatalog.AppsPermissionsManage, engineerRole.Permissions);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"manifest-manual-lock-{suffix}";
        var app = await CreateAppAsync(client, admin, appKey);
        var code = $"manual{suffix}.page.read";
        await RegisterAsync(client, admin, appKey, new AppPermissionDto(code, "查看"));

        // 解锁后清单处于未锁定；没有权限码的用户既不能解锁也不能锁定。
        var unlocked = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await UnlockAsync(client, admin, app.Id));
        Assert.False(unlocked.Locked);

        var viewerRole = await IdentityTestHelpers.GetRoleAsync(
            client, admin, IdentityConventions.ViewerRole);
        const string viewerName = "manual-lock-viewer";
        const string viewerPassword = "ManualLock@12345";
        await IdentityTestHelpers.CreateUserAsync(
            client, admin, $"{viewerName}-{suffix}", viewerPassword, roleIds: [viewerRole.Id]);
        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, $"{viewerName}-{suffix}", viewerPassword)).AccessToken;

        var deniedLock = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{app.Id}/permissions/lock",
            viewerToken));
        Assert.Equal(HttpStatusCode.Forbidden, deniedLock.StatusCode);

        var deniedUnlock = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{app.Id}/permissions/unlock",
            viewerToken));
        Assert.Equal(HttpStatusCode.Forbidden, deniedUnlock.StatusCode);

        // 未锁定状态下可以手动锁定，锁定后上传立刻被拒绝。
        var locked = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await LockAsync(client, admin, app.Id));
        Assert.True(locked.Locked);
        var blocked = await RegisterRawAsync(client, admin, appKey, new ReplaceAppPermissionsRequest(
            [new AppPermissionDto(code, "查看")]));
        Assert.Equal(
            (int)ApiErrorCode.AppsPermissionManifestLocked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Conflict));

        // 持有 apps.permissions.manage 的自建角色可以解锁。
        var manageRole = await CreateRoleAsync(
            client, admin, $"permission-manager-{suffix}", IdentityConventions.ScopePlatform, null,
            [PermissionCatalog.AppsRead, PermissionCatalog.AppsPermissionsManage]);
        const string managerName = "manual-lock-manager";
        const string managerPassword = "ManualLock@12345";
        await IdentityTestHelpers.CreateUserAsync(
            client, admin, $"{managerName}-{suffix}", managerPassword, roleIds: [manageRole.Id]);
        var managerToken = (await IdentityTestHelpers.LoginAsync(
            client, $"{managerName}-{suffix}", managerPassword)).AccessToken;

        var managerUnlocked = await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/apps/{app.Id}/permissions/unlock",
                managerToken)));
        Assert.False(managerUnlocked.Locked);
    }

    private static async Task<AppDetailDto> CreateAppAsync(
        HttpClient client,
        string token,
        string key) =>
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                token,
                new CreateAppRequest(key, key))));

    private static async Task RegisterAsync(
        HttpClient client,
        string token,
        string appKey,
        params AppPermissionDto[] permissions)
    {
        var response = await RegisterRawAsync(client, token, appKey, new ReplaceAppPermissionsRequest(permissions));
        await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(response);
    }

    private static Task<HttpResponseMessage> RegisterRawAsync(
        HttpClient client,
        string token,
        string appKey,
        ReplaceAppPermissionsRequest request) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Put,
            $"/api/v1/apps/{appKey}/permissions",
            token,
            request));

    private static Task<HttpResponseMessage> UnlockAsync(HttpClient client, string token, Guid appId) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{appId}/permissions/unlock",
            token));

    private static Task<HttpResponseMessage> LockAsync(HttpClient client, string token, Guid appId) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{appId}/permissions/lock",
            token));

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
                new CreateRoleRequest(name, DisplayName: name, Scope: scope, AppKey: appKey, Permissions: permissions))));
}
