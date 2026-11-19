using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Hubs;
using Platform.Kernel.Security;

namespace Platform.UnitTests.Kernel;

/// <summary>管理台 Hub 守卫：Touch 上行承载 exp 过期与 epoch 落后两类强制断线，当前令牌放行透传。</summary>
public sealed class AdminHubGuardFilterTests
{
    private static readonly Guid UserId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    [Fact]
    public async Task 过期令牌立即断开()
    {
        var context = new FakeHubCallerContext(UserWith(
            expSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5,
            epoch: null));
        var filter = CreateFilter(cacheCurrent: 0);

        var result = await filter.InvokeMethodAsync(
            new HubInvocationContext(context, null!, null!, null!, []),
            _ => new ValueTask<object?>("ok"));

        Assert.True(context.Aborted);
        Assert.Null(result);
    }

    [Fact]
    public async Task 落后代号的令牌被断开()
    {
        var context = new FakeHubCallerContext(UserWith(
            expSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
            epoch: 2));
        var filter = CreateFilter(cacheCurrent: 3);

        var result = await filter.InvokeMethodAsync(
            new HubInvocationContext(context, null!, null!, null!, []),
            _ => new ValueTask<object?>("ok"));

        Assert.True(context.Aborted);
        Assert.Null(result);
    }

    [Fact]
    public async Task 当前代号与未过期令牌放行透传()
    {
        var context = new FakeHubCallerContext(UserWith(
            expSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
            epoch: 3));
        var filter = CreateFilter(cacheCurrent: 3);

        var result = await filter.InvokeMethodAsync(
            new HubInvocationContext(context, null!, null!, null!, []),
            _ => new ValueTask<object?>("ok"));

        Assert.False(context.Aborted);
        Assert.Equal("ok", result);
    }

    private static AdminHubGuardFilter CreateFilter(int cacheCurrent)
    {
        var cache = new UserEpochCache(new StubClock(), TimeSpan.FromSeconds(30));
        cache.Set(UserId, cacheCurrent);
        var provider = new FakeProvider();
        var guard = new UserTokenEpochGuard(cache, provider);
        return new AdminHubGuardFilter(guard);
    }

    private static ClaimsPrincipal UserWith(long expSeconds, int? epoch)
    {
        var claims = new List<Claim>
        {
            new(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.User),
            new(PlatformClaimTypes.Subject, UserId.ToString()),
            new("exp", expSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        if (epoch is { } value)
        {
            claims.Add(new Claim(PlatformClaimTypes.TokenEpoch, value.ToString(CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class FakeProvider : IServiceScopeFactory, IServiceScope
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider { get; } = new EmptyProvider();

        public void Dispose()
        {
        }

        public static ValueTask DisposeAsync() => default;

        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class FakeHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public bool Aborted { get; private set; }

        public override string ConnectionId => "admin-guard-test";

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => Aborted = true;
    }
}
