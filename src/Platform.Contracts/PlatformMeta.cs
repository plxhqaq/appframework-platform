namespace Platform.Contracts;

/// <summary>当前主体自述（<c>GET /api/v1/platform/whoami</c> 的响应体）。</summary>
/// <param name="IsAuthenticated">是否已认证。</param>
/// <param name="UserId">用户 id（<c>sub</c>）。</param>
/// <param name="UserName">用户名（<c>name</c>）。</param>
/// <param name="IsService">是否 M2M 主体。</param>
/// <param name="IsTemporary">是否临时令牌。</param>
/// <param name="AppKey">当前应用键（<c>app</c> 首个值）。</param>
/// <param name="Roles">角色（<c>role</c>）。</param>
/// <param name="Permissions">权限码（<c>perms</c>）。</param>
/// <param name="AppKeys">可访问应用键（<c>app</c> 全部值）。</param>
public sealed record WhoAmIResponse(
    bool IsAuthenticated,
    string? UserId,
    string? UserName,
    bool IsService,
    bool IsTemporary,
    string? AppKey,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> AppKeys);
