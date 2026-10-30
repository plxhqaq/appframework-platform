using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Platform.Contracts;
using Platform.Contracts.Identity;

namespace Platform.IntegrationTests.Support;

/// <summary>身份相关 HTTP 用例的公共动作，避免每个用例重复拼请求与拆信封。</summary>
internal static class IdentityTestHelpers
{
    public const string AdminUserName = IdentityConventions.AdminUserName;
    public const string AdminPassword = "Admin@12345";

    public static async Task<LoginResponse> LoginAsync(
        HttpClient client,
        string userName,
        string password,
        string? appKey = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest(userName, password, appKey));
        return await ReadDataAsync<LoginResponse>(response);
    }

    public static async Task<string> LoginAsAdminAsync(HttpClient client) =>
        (await LoginAsync(client, AdminUserName, AdminPassword)).AccessToken;

    public static async Task<RoleSummaryDto> GetRoleAsync(HttpClient client, string token, string name)
    {
        var roles = await ReadDataAsync<List<RoleSummaryDto>>(
            await client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/admin/identity/roles", token)));
        return roles.Single(x => string.Equals(x.Name, name, StringComparison.Ordinal));
    }

    public static async Task<UserDetailDto> CreateUserAsync(
        HttpClient client,
        string token,
        string userName,
        string password,
        string? displayName = null,
        IReadOnlyList<Guid>? roleIds = null,
        bool isEnabled = true,
        string? idempotencyKey = null)
    {
        using var request = Authorized(
            HttpMethod.Post,
            "/api/v1/admin/identity/users",
            token,
            new CreateUserRequest(userName, password, displayName, isEnabled, roleIds));
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(PlatformHeaders.IdempotencyKey, idempotencyKey);
        }

        return await ReadDataAsync<UserDetailDto>(await client.SendAsync(request));
    }

    public static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<T>>();
        Assert.NotNull(payload);
        Assert.True(response.IsSuccessStatusCode, payload!.Message);
        Assert.True(payload.IsSuccess, payload.Message);
        Assert.NotNull(payload.Data);
        return payload.Data!;
    }

    public static async Task<int> ReadErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.NotNull(payload);
        Assert.False(payload!.IsSuccess);
        return payload.Code;
    }
}
