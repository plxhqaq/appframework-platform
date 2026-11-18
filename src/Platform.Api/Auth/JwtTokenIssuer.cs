using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Platform.Contracts;
using Platform.Kernel.Abstractions;

namespace Platform.Api.Auth;

/// <summary>访问令牌签发。身份模块与 M2M 端点通过 <see cref="IAccessTokenIssuer"/> 使用它。</summary>
public sealed class JwtTokenIssuer : IAccessTokenIssuer
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    /// <summary>构造并校验签名密钥强度。</summary>
    public JwtTokenIssuer(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey 至少需要 32 个字符（HMAC-SHA256 最小密钥长度）。");
        }

        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
    }

    /// <summary>签发访问令牌。</summary>
    /// <param name="subject">用户 id。</param>
    /// <param name="name">用户名。</param>
    /// <param name="roles">角色名。</param>
    /// <param name="permissions">权限码（<c>*</c> 表示管理员）。</param>
    /// <param name="appKeys">可访问的应用键。</param>
    /// <param name="appKey">当前应用键。</param>
    /// <param name="temporary">是否临时令牌。</param>
    /// <param name="principalKind">主体类型，默认 <c>user</c>。</param>
    /// <param name="machineCode">绑定的机器码。</param>
    /// <param name="installGuid">绑定的安装实例标识。</param>
    /// <param name="lifetime">有效期；默认取配置。</param>
    public IssuedToken Issue(
        string subject,
        string name,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        IEnumerable<string>? appKeys = null,
        string? appKey = null,
        bool temporary = false,
        string principalKind = PlatformPrincipalKinds.User,
        string? machineCode = null,
        Guid? installGuid = null,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(Math.Max(1, _options.AccessTokenMinutes)));

        var claims = new List<Claim>
        {
            new(PlatformClaimTypes.Subject, subject),
            new(PlatformClaimTypes.Name, name),
            new(PlatformClaimTypes.JwtId, Guid.CreateVersion7().ToString("N")),
            new(PlatformClaimTypes.PrincipalKind, principalKind),
            new(PlatformClaimTypes.Temporary, temporary ? "true" : "false"),
        };

        foreach (var role in roles ?? [])
        {
            claims.Add(new Claim(PlatformClaimTypes.Role, role));
        }

        foreach (var permission in permissions ?? [])
        {
            claims.Add(new Claim(PlatformClaimTypes.Permission, permission));
        }

        // app 声明是隔离轴：既要去重，也要把"当前应用"固定排在首位（ICurrentPrincipal.AppKey 取第一个值）
        var appList = (appKeys ?? [])
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(appKey))
        {
            appList.Remove(appKey);
            appList.Insert(0, appKey);
        }

        foreach (var key in appList)
        {
            claims.Add(new Claim(PlatformClaimTypes.App, key));
        }

        if (!string.IsNullOrWhiteSpace(machineCode))
        {
            claims.Add(new Claim(PlatformClaimTypes.MachineCode, machineCode));
        }

        if (installGuid is { } install && install != Guid.Empty)
        {
            claims.Add(new Claim(PlatformClaimTypes.InstallGuid, install.ToString("D")));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _credentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <inheritdoc />
    public IssuedToken Issue(AccessTokenSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Issue(
            subject: spec.Subject,
            name: spec.Name,
            roles: spec.Roles,
            permissions: spec.Permissions,
            appKeys: spec.AppKeys,
            appKey: spec.AppKey,
            temporary: spec.Temporary,
            principalKind: spec.PrincipalKind,
            machineCode: spec.MachineCode,
            installGuid: spec.InstallGuid,
            lifetime: spec.Lifetime);
    }
}
