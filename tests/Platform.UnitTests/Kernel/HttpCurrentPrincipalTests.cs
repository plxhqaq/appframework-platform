using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;

namespace Platform.UnitTests.Kernel;

/// <summary>
/// 当前主体：所有业务查询的 <c>app_id</c> 隔离轴都从这里取，
/// 所以声明名一旦写错就会变成「静默空隔离」——这里逐项断言。
/// </summary>
public sealed class HttpCurrentPrincipalTests
{
    [Fact]
    public void 无请求上下文时一切为空且未认证()
    {
        var principal = CreatePrincipal(user: null);

        Assert.False(principal.IsAuthenticated);
        Assert.Null(principal.UserId);
        Assert.Null(principal.UserName);
        Assert.Null(principal.AppKey);
        Assert.Empty(principal.Roles);
        Assert.Empty(principal.Permissions);
        Assert.Empty(principal.AppKeys);
        Assert.False(principal.IsTemporary);
        Assert.False(principal.IsService);
    }

    [Fact]
    public void 读取短名声明()
    {
        var principal = CreatePrincipal(CreateUser());

        Assert.True(principal.IsAuthenticated);
        Assert.Equal("user-1", principal.UserId);
        Assert.Equal("alice", principal.UserName);
        Assert.True(principal.IsTemporary);
        Assert.False(principal.IsService);
    }

    [Fact]
    public void 多值声明按原顺序展开()
    {
        var principal = CreatePrincipal(CreateUser());

        Assert.Equal(["admin", "operator"], principal.Roles);
        Assert.Equal(["demo", "other"], principal.AppKeys);
        Assert.Equal("demo", principal.AppKey);
    }

    [Fact]
    public void 权限判定区分通配与具体码()
    {
        var principal = CreatePrincipal(CreateUser());

        Assert.True(principal.HasPermission("identity.user.read"));
        Assert.False(principal.HasPermission("document.manage"));
        Assert.True(principal.HasAnyPermission("document.manage", "identity.user.read"));
        Assert.False(principal.HasAnyPermission("document.manage", "license.issue"));
        Assert.False(principal.HasPermission(" "));
        Assert.False(principal.HasAnyPermission());
    }

    [Fact]
    public void 通配权限放行任意权限码()
    {
        var user = CreateUser([new Claim(PlatformClaimTypes.Permission, PermissionCatalog.Wildcard)]);
        var principal = CreatePrincipal(user);

        Assert.True(principal.HasPermission("anything.at.all"));
        Assert.True(principal.HasPermission(PermissionCatalog.PlatformPermissions[0].Code));
    }

    [Fact]
    public void 服务主体按主体类型识别()
    {
        var user = CreateUser([new Claim(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.Service)]);
        var principal = CreatePrincipal(user);

        Assert.True(principal.IsService);
    }

    private static HttpCurrentPrincipal CreatePrincipal(ClaimsPrincipal? user) =>
        new(new FakeHttpContextAccessor(user));

    private static ClaimsPrincipal CreateUser(IEnumerable<Claim>? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new(PlatformClaimTypes.Subject, "user-1"),
            new(PlatformClaimTypes.Name, "alice"),
            new(PlatformClaimTypes.Role, "admin"),
            new(PlatformClaimTypes.Role, "operator"),
            new(PlatformClaimTypes.App, "demo"),
            new(PlatformClaimTypes.App, "other"),
            new(PlatformClaimTypes.Temporary, "true"),
            new(PlatformClaimTypes.Permission, "identity.user.read"),
            new(PlatformClaimTypes.Permission, "identity.role.read"),
        };

        if (extraClaims is not null)
        {
            claims.AddRange(extraClaims);
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public FakeHttpContextAccessor(ClaimsPrincipal? user)
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user };
        }

        public HttpContext? HttpContext { get; set; }
    }
}
