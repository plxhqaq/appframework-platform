using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;

namespace Platform.Modules.Apps.Services;

/// <summary>API Key → M2M 访问令牌。</summary>
public interface IM2MTokenService
{
    /// <summary>校验凭据并签发服务主体令牌。</summary>
    Task<M2MTokenResponse> IssueAsync(
        string appKey,
        string apiKey,
        IReadOnlyList<string>? requestedScopes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// M2M 令牌签发：应用键 + API Key 换取短期 JWT。
/// </summary>
/// <remarks>
/// <b>只减不增</b>：请求的 scope 必须是 Key 已授予 scope 的子集，否则拒绝——
/// 否则持有一个窄权限 Key 的调用方可以自行升级成管理员权限。
/// </remarks>
public sealed class M2MTokenService : IM2MTokenService
{
    private readonly IApiKeyService _apiKeys;
    private readonly IAccessTokenIssuer _issuer;

    /// <summary>构造。</summary>
    public M2MTokenService(IApiKeyService apiKeys, IAccessTokenIssuer issuer)
    {
        _apiKeys = apiKeys;
        _issuer = issuer;
    }

    /// <inheritdoc />
    public async Task<M2MTokenResponse> IssueAsync(
        string appKey,
        string apiKey,
        IReadOnlyList<string>? requestedScopes,
        CancellationToken cancellationToken = default)
    {
        var authenticated = await _apiKeys.AuthenticateAsync(appKey, apiKey, cancellationToken);

        var granted = authenticated.Scopes;
        var effective = granted;
        if (requestedScopes is { Count: > 0 })
        {
            var unknown = requestedScopes
                .Where(scope => !granted.Contains(scope, StringComparer.Ordinal))
                .ToList();
            if (unknown.Count > 0)
            {
                throw new BusinessException(
                    ApiErrorCode.AppsApiKeyScopeDenied,
                    $"API Key 未被授予权限：{string.Join(", ", unknown)}。");
            }

            effective = [.. requestedScopes.Distinct(StringComparer.Ordinal)];
        }

        var issued = _issuer.Issue(new AccessTokenSpec
        {
            Subject = $"apikey:{authenticated.Key.Id:N}",
            Name = $"{authenticated.App.Name} ({authenticated.Key.Name})",
            Roles = ["service"],
            Permissions = effective,
            AppKeys = [authenticated.App.Key],
            AppKey = authenticated.App.Key,
            PrincipalKind = PlatformPrincipalKinds.Service,
        });

        return new M2MTokenResponse(
            issued.AccessToken,
            "Bearer",
            issued.ExpiresAt,
            authenticated.App.Key,
            effective,
            authenticated.App.PermissionsLocked);
    }
}
