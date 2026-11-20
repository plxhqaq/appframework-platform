using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Permissions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>从开发令牌到 M2M 令牌的完整 HTTP 链路，验证权限、幂等与主体声明。</summary>
public sealed class AppsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private static readonly string[] WildcardPermissions = ["*"];
    private static readonly string[] DemoApps = ["demo-ci"];
    private const string TestAppKey = "demo-ci";

    private readonly PlatformApiFactory _factory;

    public AppsEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 创建应用签发密钥换令牌并确认服务主体()
    {
        using var client = _factory.CreateClient();
        var adminToken = await GetAdminTokenAsync(client);

        var createResponse = await client.SendAsync(CreateAppRequestMessage(adminToken));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ApiResult<AppDetailDto>>();
        Assert.NotNull(created);
        Assert.True(created.IsSuccess);
        Assert.NotNull(created.Data);
        Assert.Equal(TestAppKey, created.Data.Key);

        // 同键重放：返回同一个应用，不重复创建
        var replay = await client.SendAsync(CreateAppRequestMessage(adminToken));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<ApiResult<AppDetailDto>>();
        Assert.Equal(created.Data.Id, replayed!.Data!.Id);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/apps");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var list = await (await client.SendAsync(listRequest))
            .Content.ReadFromJsonAsync<ApiResult<List<AppSummaryDto>>>();
        Assert.Contains(list!.Data!, x => x.Id == created.Data.Id);

        // 签发 API Key：明文只出现一次
        using var keyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/admin/apps/{created.Data.Id}/keys")
        {
            Content = JsonContent.Create(new CreateApiKeyRequest(
                "CI",
                [PermissionCatalog.VersionsReleaseRead])),
        };
        keyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var keyResponse = await client.SendAsync(keyRequest);
        var keyResult = await keyResponse.Content.ReadFromJsonAsync<ApiResult<CreatedApiKeyDto>>();
        Assert.True(keyResult!.IsSuccess);
        var apiKey = keyResult.Data!.Plaintext;
        Assert.StartsWith(AppConventions.ApiKeyPrefix, apiKey, StringComparison.Ordinal);

        // 换 M2M 令牌
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apps/token");
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.AppKey, TestAppKey);
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.ApiKey, apiKey);
        var tokenResponse = await client.SendAsync(tokenRequest);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokenResult = await tokenResponse.Content.ReadFromJsonAsync<ApiResult<M2MTokenResponse>>();
        Assert.True(tokenResult!.IsSuccess);
        Assert.Equal(TestAppKey, tokenResult.Data!.AppKey);

        // 服务主体：isService=true、appKey=demo、权限等于 Key 的 scopes
        using var whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/platform/whoami");
        whoamiRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenResult.Data.AccessToken);
        var whoami = await (await client.SendAsync(whoamiRequest))
            .Content.ReadFromJsonAsync<ApiResult<WhoAmIResponsePayload>>();
        Assert.True(whoami!.Data!.IsService);
        Assert.Equal(TestAppKey, whoami.Data.AppKey);
        Assert.Contains("versions.release.read", whoami.Data.Permissions);

        // 禁用应用后，已签发的 Key 也不能再换令牌
        using var disableRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/admin/apps/{created.Data.Id}")
        {
            Content = JsonContent.Create(new UpdateAppRequest(IsEnabled: false)),
        };
        disableRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(disableRequest)).StatusCode);

        var disabledResponse = await client.SendAsync(await CloneAsync(tokenRequest));
        Assert.Equal(HttpStatusCode.Forbidden, disabledResponse.StatusCode);
        var disabled = await disabledResponse.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.Equal((int)ApiErrorCode.AppsDisabled, disabled!.Code);
    }

    [Fact]
    public async Task API密钥须先吊销才能删除()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = $"key-del-{Guid.NewGuid().ToString("N")[..8]}";

        var app = await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/apps", token, new CreateAppRequest(appKey, "密钥删除测试"))));

        var issued = await IdentityTestHelpers.ReadDataAsync<CreatedApiKeyDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/apps/{app.Id}/keys",
                token,
                new CreateApiKeyRequest("现场设备", [PermissionCatalog.VersionsReleaseRead]))));

        // 仍在生效的密钥只能吊销，不能直接删行
        var busy = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}/keys/{issued.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(busy, HttpStatusCode.Conflict));

        var revoked = await IdentityTestHelpers.ReadDataAsync<ApiKeySummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/keys/{issued.Id}/revoke", token)));
        Assert.NotNull(revoked.RevokedAt);

        Assert.True(await IdentityTestHelpers.ReadDataAsync<bool>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}/keys/{issued.Id}", token))));

        var keys = await IdentityTestHelpers.ReadDataAsync<List<ApiKeySummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}/keys", token)));
        Assert.DoesNotContain(keys, x => x.Id == issued.Id);

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}/keys/{issued.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.AppsApiKeyNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));
    }

    private static HttpRequestMessage CreateAppRequestMessage(string adminToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/apps")
        {
            Content = JsonContent.Create(new CreateAppRequest(TestAppKey, "演示应用")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.TryAddWithoutValidation(PlatformHeaders.IdempotencyKey, "create-demo-1");
        return request;
    }

    private static async Task<string> GetAdminTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/dev/token", new
        {
            subject = "admin-1",
            name = "集成测试管理员",
            permissions = WildcardPermissions,
            apps = DemoApps,
            appKey = TestAppKey,
        });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<DevTokenPayload>>();
        return payload!.Data!.AccessToken;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync();
            clone.Content = new StringContent(body);
            clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }

        return clone;
    }

    private sealed record DevTokenPayload(string AccessToken);

    private sealed record WhoAmIResponsePayload(
        bool IsAuthenticated,
        bool IsService,
        string? AppKey,
        IReadOnlyList<string> Permissions);
}
