using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Platform.Api.Auth;
using Platform.Contracts;
using Platform.Kernel.Abstractions;

namespace Platform.UnitTests.Api;

/// <summary>
/// 令牌声明：名字写错会被静默改写（<c>MapInboundClaims</c>），app 重复会让隔离轴取值不确定，
/// 所以这里直接读回原始 JWT 断言声明。
/// </summary>
public sealed class JwtTokenIssuerTests
{
    private static readonly JwtOptions DefaultOptions = new()
    {
        Issuer = "appframework-platform",
        Audience = "appframework",
        SigningKey = "unit-test-signing-key-at-least-32-chars",
        AccessTokenMinutes = 30,
    };

    [Fact]
    public void 声明使用短名且写入主体信息()
    {
        var token = Issue(temporary: true);

        var claims = ReadClaims(token);
        Assert.Equal("user-1", Single(claims, PlatformClaimTypes.Subject));
        Assert.Equal("alice", Single(claims, PlatformClaimTypes.Name));
        Assert.Equal("true", Single(claims, PlatformClaimTypes.Temporary));
        Assert.Equal(PlatformPrincipalKinds.User, Single(claims, PlatformClaimTypes.PrincipalKind));
        Assert.False(string.IsNullOrWhiteSpace(Single(claims, PlatformClaimTypes.JwtId)));
    }

    [Fact]
    public void 多值声明逐项写入()
    {
        var token = Issue();

        var claims = ReadClaims(token);
        Assert.Equal(["admin", "operator"], Values(claims, PlatformClaimTypes.Role));
        Assert.Equal(["apps.read", "apps.manage"], Values(claims, PlatformClaimTypes.Permission));
    }

    [Fact]
    public void 当前应用键排在首位且不重复()
    {
        var token = Issue(appKeys: ["other", "demo"], appKey: "demo");

        var claims = ReadClaims(token);
        Assert.Equal(["demo", "other"], Values(claims, PlatformClaimTypes.App));
    }

    [Fact]
    public void 未传应用键列表时也能写入当前应用()
    {
        var token = CreateIssuer().Issue(
            subject: "user-1",
            name: "alice",
            appKeys: null,
            appKey: "demo");

        Assert.Equal(["demo"], Values(ReadClaims(token), PlatformClaimTypes.App));
    }

    [Fact]
    public void 签名密钥过短时构造失败()
    {
        var weak = new JwtOptions { Issuer = "i", Audience = "a", SigningKey = "short", AccessTokenMinutes = 30 };

        var exception = Assert.Throws<InvalidOperationException>(() => new JwtTokenIssuer(Options.Create(weak)));
        Assert.Contains("SigningKey", exception.Message, StringComparison.Ordinal);
    }

    private static JwtTokenIssuer CreateIssuer() => new(Options.Create(DefaultOptions));

    private static IssuedToken Issue(
        bool temporary = false,
        string[]? appKeys = null,
        string? appKey = "demo") =>
        CreateIssuer().Issue(
            subject: "user-1",
            name: "alice",
            roles: ["admin", "operator"],
            permissions: ["apps.read", "apps.manage"],
            appKeys: appKeys ?? ["demo"],
            appKey: appKey,
            temporary: temporary);

    private static List<(string Type, string Value)> ReadClaims(IssuedToken token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        return [.. jwt.Claims.Select(static c => (c.Type, c.Value))];
    }

    private static string? Single(List<(string Type, string Value)> claims, string type) =>
        claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.Ordinal)).Value;

    private static string[] Values(List<(string Type, string Value)> claims, string type) =>
        [.. claims.Where(c => string.Equals(c.Type, type, StringComparison.Ordinal)).Select(static c => c.Value)];
}
