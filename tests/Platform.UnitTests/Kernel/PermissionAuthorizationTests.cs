using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Security;

namespace Platform.UnitTests.Kernel;

/// <summary>
/// 权限判定：<c>perms</c> 声明是服务端唯一事实源，前端隐藏按钮不算安全边界，
/// 所以这里逐条钉死「无认证 / 任一 / 全部 / 通配」四种情形。
/// </summary>
public sealed class PermissionAuthorizationTests
{
    [Fact]
    public void 权限要求不能为空()
    {
        Assert.Throws<ArgumentException>(() => new PermissionRequirement([]));
    }

    [Fact]
    public async Task 未认证主体不通过()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var requirement = new PermissionRequirement(["identity.user.read"]);

        var context = await EvaluateAsync(user, requirement);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task 命中任一权限码即通过()
    {
        var user = CreateUser("identity.role.read", "identity.user.read");
        var requirement = new PermissionRequirement(["identity.user.read", "versions.release.read"]);

        var context = await EvaluateAsync(user, requirement);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task 任一模式下缺一个仍通过()
    {
        var user = CreateUser("identity.user.read");
        var requirement = new PermissionRequirement(["identity.user.read", "versions.release.read"]);

        var context = await EvaluateAsync(user, requirement);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task 全部模式下缺一个则不通过()
    {
        var user = CreateUser("identity.user.read");
        var requirement = new PermissionRequirement(["identity.user.read", "versions.release.read"], requireAll: true);

        var context = await EvaluateAsync(user, requirement);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task 全部模式下全部命中则通过()
    {
        var user = CreateUser("identity.user.read", "versions.release.read");
        var requirement = new PermissionRequirement(["identity.user.read", "versions.release.read"], requireAll: true);

        var context = await EvaluateAsync(user, requirement);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task 通配符等同平台管理员()
    {
        var user = CreateUser(PermissionCatalog.Wildcard);
        var requirement = new PermissionRequirement([PermissionCatalog.PlatformPermissions[0].Code], requireAll: true);

        var context = await EvaluateAsync(user, requirement);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public void 策略名带模式前缀与全部权限码()
    {
        var any = PermissionPolicyProvider.PolicyName(["a.b.c", "d.e.f"]);
        var all = PermissionPolicyProvider.PolicyName(["a.b.c", "d.e.f"], requireAll: true);

        Assert.Equal("perm:any|a.b.c|d.e.f", any);
        Assert.Equal("perm:all|a.b.c|d.e.f", all);
        Assert.Throws<ArgumentException>(() => PermissionPolicyProvider.PolicyName([" ", ""]));
    }

    [Fact]
    public async Task 动态策略按策略名还原权限要求()
    {
        var provider = CreateProvider();

        var anyPolicy = await provider.GetPolicyAsync(PermissionPolicyProvider.PolicyName(["identity.user.read", "identity.role.read"]));
        var allPolicy = await provider.GetPolicyAsync(PermissionPolicyProvider.PolicyName(["identity.user.read", "identity.role.read"], requireAll: true));

        Assert.NotNull(anyPolicy);
        Assert.NotNull(allPolicy);

        // 策略同时带 RequireAuthenticatedUser()，所以只挑出权限要求这一条
        var anyRequirement = Assert.Single(anyPolicy.Requirements.OfType<PermissionRequirement>());
        var allRequirement = Assert.Single(allPolicy.Requirements.OfType<PermissionRequirement>());

        Assert.False(anyRequirement.RequireAll);
        Assert.True(allRequirement.RequireAll);
        Assert.Equal(2, anyRequirement.Codes.Count);
    }

    [Fact]
    public async Task 畸形策略名返回空而不是抛异常()
    {
        var provider = CreateProvider();

        Assert.Null(await provider.GetPolicyAsync("perm:any"));
        Assert.Null(await provider.GetPolicyAsync("perm:any|"));
    }

    [Fact]
    public async Task 非权限策略交给默认提供者()
    {
        var provider = CreateProvider();

        // 默认提供者没有登记过这条策略，返回 null 而不是抛异常
        Assert.Null(await provider.GetPolicyAsync("some-unregistered-policy"));
        Assert.NotNull(await provider.GetDefaultPolicyAsync());
    }

    private static PermissionPolicyProvider CreateProvider() =>
        new(Options.Create(new AuthorizationOptions()));

    private static ClaimsPrincipal CreateUser(params string[] permissions)
    {
        var claims = permissions.Select(static code => new Claim(PlatformClaimTypes.Permission, code));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static async Task<AuthorizationHandlerContext> EvaluateAsync(
        ClaimsPrincipal user,
        PermissionRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PermissionAuthorizationHandler().HandleAsync(context);
        return context;
    }
}
