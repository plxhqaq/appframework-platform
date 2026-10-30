using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Contracts.Permissions;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 从 <see cref="HttpContext.User"/> 读取主体信息。
/// </summary>
/// <remarks>
/// 前提：JWT 必须配置 <c>MapInboundClaims = false</c>，否则 <c>sub</c>/<c>perms</c> 会被改写成
/// <c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/*</c>，这里就全读不到（现网踩过）。
/// </remarks>
public sealed class HttpCurrentPrincipal : ICurrentPrincipal
{
    private readonly IHttpContextAccessor _accessor;
    private IReadOnlySet<string>? _permissions;
    private IReadOnlyList<string>? _roles;
    private IReadOnlyList<string>? _appKeys;

    /// <summary>构造。</summary>
    public HttpCurrentPrincipal(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    /// <inheritdoc />
    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public string? UserId => User?.FindFirst(PlatformClaimTypes.Subject)?.Value;

    /// <inheritdoc />
    public string? UserName => User?.FindFirst(PlatformClaimTypes.Name)?.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => _roles ??= Values(PlatformClaimTypes.Role);

    /// <inheritdoc />
    public IReadOnlySet<string> Permissions =>
        _permissions ??= Values(PlatformClaimTypes.Permission).ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> AppKeys => _appKeys ??= Values(PlatformClaimTypes.App);

    /// <inheritdoc />
    public string? AppKey => AppKeys.Count > 0 ? AppKeys[0] : null;

    /// <inheritdoc />
    public bool IsTemporary => string.Equals(
        User?.FindFirst(PlatformClaimTypes.Temporary)?.Value,
        "true",
        StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsService => string.Equals(
        User?.FindFirst(PlatformClaimTypes.PrincipalKind)?.Value,
        PlatformPrincipalKinds.Service,
        StringComparison.Ordinal);

    /// <inheritdoc />
    public bool HasPermission(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return Permissions.Contains(PermissionCatalog.Wildcard) || Permissions.Contains(code);
    }

    /// <inheritdoc />
    public bool HasAnyPermission(params string[] codes)
    {
        if (codes is null || codes.Length == 0) return false;
        if (Permissions.Contains(PermissionCatalog.Wildcard)) return true;
        foreach (var code in codes)
        {
            if (Permissions.Contains(code)) return true;
        }
        return false;
    }

    private string[] Values(string claimType) =>
        User?.FindAll(claimType).Select(static c => c.Value).ToArray() ?? [];
}
