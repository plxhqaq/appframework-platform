using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Api.Auth;
using Platform.Kernel.Http;

namespace Platform.Api.Endpoints;

/// <summary>
/// 开发令牌端点（<b>仅 Development + 显式开关</b>）。
/// </summary>
/// <remarks>
/// 存在的理由：S1 之前还没有登录接口，但没有令牌就无法验证"JWT 声明 → 权限判定"这条链路。
/// 它把链路验证从 S2 提前到 S0，代价只是二十行代码。
/// 生产环境（非 Development）或 <c>Jwt:AllowDevTokenEndpoint=false</c> 时该端点不会被注册。
/// </remarks>
public static class DevTokenEndpoints
{
    /// <summary>挂载开发令牌端点。</summary>
    public static IEndpointRouteBuilder MapDevTokenEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/api/v1/dev/token", (HttpContext context, DevTokenRequest? request, JwtTokenIssuer issuer) =>
            {
                var payload = request ?? new DevTokenRequest();

                var permissions = payload.Permissions is { Length: > 0 }
                    ? payload.Permissions
                    : ["*"];

                var issued = issuer.Issue(
                    subject: payload.Subject ?? "dev-admin",
                    name: payload.Name ?? "开发账号",
                    roles: payload.Roles ?? ["platform-admin"],
                    permissions: permissions,
                    appKeys: payload.Apps ?? ["demo"],
                    appKey: payload.AppKey ?? "demo",
                    temporary: payload.Temporary);

                return ApiResults.Ok(context, new DevTokenResponse(
                    issued.AccessToken,
                    issued.ExpiresAt,
                    permissions,
                    payload.AppKey ?? "demo"));
            })
            .WithTags("dev")
            .WithSummary("签发开发令牌（仅开发环境）")
            .AllowAnonymous();

        return api;
    }
}

/// <summary>开发令牌请求。字段全可空，缺省即管理员通配权限。</summary>
/// <param name="Subject">用户 id。</param>
/// <param name="Name">用户名。</param>
/// <param name="Roles">角色名。</param>
/// <param name="Permissions">权限码；缺省 <c>["*"]</c>。</param>
/// <param name="Apps">可访问应用键；缺省 <c>["demo"]</c>。</param>
/// <param name="AppKey">当前应用键；缺省 <c>demo</c>。</param>
/// <param name="Temporary">是否临时令牌。</param>
public sealed record DevTokenRequest(
    string? Subject = null,
    string? Name = null,
    string[]? Roles = null,
    string[]? Permissions = null,
    string[]? Apps = null,
    string? AppKey = null,
    bool Temporary = false);

/// <summary>开发令牌响应。</summary>
/// <param name="AccessToken">JWT。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <param name="Permissions">写入令牌的权限码。</param>
/// <param name="AppKey">写入令牌的应用键。</param>
public sealed record DevTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Permissions,
    string AppKey);
