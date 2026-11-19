using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Platform.Modules.Devices.Hubs;
using Platform.Modules.Devices.Services;

namespace Platform.UnitTests.Devices;

/// <summary>设备 Hub 守卫：上行调用前校验令牌 exp 与停用待踢标记，命中即断开；否则放行。</summary>
public sealed class DeviceHubGuardFilterTests
{
    [Fact]
    public async Task 过期令牌立即断开且不执行业务方法()
    {
        var context = new FakeHubCallerContext(UserWithExp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10));
        var filter = new DeviceHubGuardFilter(new InstallationConnectionRegistry());
        var invoked = false;

        var result = await filter.InvokeMethodAsync(
            CreateInvocation(context),
            _ =>
            {
                invoked = true;
                return new ValueTask<object?>("ok");
            });

        Assert.True(context.Aborted);
        Assert.False(invoked);
        Assert.Null(result);
    }

    [Fact]
    public async Task 未过期令牌放行并透传返回值()
    {
        var context = new FakeHubCallerContext(UserWithExp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600));
        var filter = new DeviceHubGuardFilter(new InstallationConnectionRegistry());

        var result = await filter.InvokeMethodAsync(
            CreateInvocation(context),
            _ => new ValueTask<object?>("ok"));

        Assert.False(context.Aborted);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task 缺少exp声明时放行()
    {
        var context = new FakeHubCallerContext(new ClaimsPrincipal(new ClaimsIdentity()));
        var filter = new DeviceHubGuardFilter(new InstallationConnectionRegistry());

        var result = await filter.InvokeMethodAsync(
            CreateInvocation(context),
            _ => new ValueTask<object?>("ok"));

        Assert.False(context.Aborted);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task 停用待踢标记命中即断开()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();
        registry.Register("demo", installationId, Guid.NewGuid(), Guid.NewGuid(), "guard-test-connection");
        registry.MarkInstallationDisabled(installationId);

        var context = new FakeHubCallerContext(UserWithExp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600));
        var filter = new DeviceHubGuardFilter(registry);
        var invoked = false;

        var result = await filter.InvokeMethodAsync(
            CreateInvocation(context),
            _ =>
            {
                invoked = true;
                return new ValueTask<object?>("ok");
            });

        Assert.True(context.Aborted);
        Assert.False(invoked);
        Assert.Null(result);
    }

    [Fact]
    public async Task 注销连接后待踢标记不残留()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();
        registry.Register("demo", installationId, Guid.NewGuid(), Guid.NewGuid(), "guard-test-connection");
        registry.MarkInstallationDisabled(installationId);
        registry.Unregister("guard-test-connection");

        var context = new FakeHubCallerContext(UserWithExp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600));
        var filter = new DeviceHubGuardFilter(registry);

        var result = await filter.InvokeMethodAsync(
            CreateInvocation(context),
            _ => new ValueTask<object?>("ok"));

        Assert.False(context.Aborted);
        Assert.Equal("ok", result);
    }

    private static ClaimsPrincipal UserWithExp(long expiresAtSeconds) =>
        new(new ClaimsIdentity([new Claim("exp", expiresAtSeconds.ToString(CultureInfo.InvariantCulture))]));

    private static HubInvocationContext CreateInvocation(HubCallerContext context) =>
        new(context, null!, null!, null!, []);

    private sealed class FakeHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public bool Aborted { get; private set; }

        public override string ConnectionId => "guard-test-connection";

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => Aborted = true;
    }
}