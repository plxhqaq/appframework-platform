using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Security;

namespace Platform.UnitTests.Kernel;

/// <summary>
/// 用户级 epoch 守卫（D7）：落后代号拒绝、窗口内命中免查库，
/// 服务主体/开发令牌/用户行缺失/未认证四类放行条件不得误伤。
/// </summary>
public sealed class UserTokenEpochGuardTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task 缓存命中的落后代号被拒绝()
    {
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.FromSeconds(30));
        cache.Set(UserId, 5);
        var guard = CreateGuard(cache, null);

        Assert.False(await guard.IsCurrentAsync(User(epoch: 4)));
        Assert.True(await guard.IsCurrentAsync(User(epoch: 5)));
    }

    [Fact]
    public async Task 命中缓存时不触目录查库()
    {
        var directory = new FakeDirectory(9);
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.FromSeconds(30));
        var guard = CreateGuard(cache, directory);

        Assert.True(await guard.IsCurrentAsync(User(epoch: 9)));
        Assert.Equal(1, directory.Calls);

        Assert.True(await guard.IsCurrentAsync(User(epoch: 9)));
        Assert.Equal(1, directory.Calls);
    }

    [Fact]
    public async Task 缺失代号声明按0比对()
    {
        var directory = new FakeDirectory(1);
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.Zero);
        var guard = CreateGuard(cache, directory);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.User),
                new Claim(PlatformClaimTypes.Subject, UserId.ToString()),
            ],
            "test"));

        Assert.False(await guard.IsCurrentAsync(user));
    }

    [Fact]
    public async Task 服务主体豁免()
    {
        var directory = new FakeDirectory(5);
        var guard = CreateGuard(new UserEpochCache(new FakeClock(), TimeSpan.Zero), directory);

        var service = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.Service),
                new Claim(PlatformClaimTypes.Subject, UserId.ToString()),
                new Claim(PlatformClaimTypes.TokenEpoch, "0"),
            ],
            "test"));

        Assert.True(await guard.IsCurrentAsync(service));
        Assert.Equal(0, directory.Calls);
    }

    [Fact]
    public async Task 非Guid主体放行()
    {
        var directory = new FakeDirectory(5);
        var guard = CreateGuard(new UserEpochCache(new FakeClock(), TimeSpan.Zero), directory);

        var dev = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.User),
                new Claim(PlatformClaimTypes.Subject, "dev-admin"),
                new Claim(PlatformClaimTypes.TokenEpoch, "0"),
            ],
            "test"));

        Assert.True(await guard.IsCurrentAsync(dev));
        Assert.Equal(0, directory.Calls);
    }

    [Fact]
    public async Task 用户行不存在时放行()
    {
        var directory = new FakeDirectory(current: null);
        var guard = CreateGuard(new UserEpochCache(new FakeClock(), TimeSpan.Zero), directory);

        Assert.True(await guard.IsCurrentAsync(User(epoch: 0)));
    }

    [Fact]
    public async Task 未注册目录时放行()
    {
        var guard = CreateGuard(new UserEpochCache(new FakeClock(), TimeSpan.Zero), directory: null);

        Assert.True(await guard.IsCurrentAsync(User(epoch: 0)));
    }

    [Fact]
    public async Task 未认证主体放行()
    {
        var guard = CreateGuard(new UserEpochCache(new FakeClock(), TimeSpan.Zero), new FakeDirectory(5));

        Assert.True(await guard.IsCurrentAsync(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.True(await guard.IsCurrentAsync(null));
    }

    private static UserTokenEpochGuard CreateGuard(UserEpochCache cache, FakeDirectory? directory)
    {
        IServiceProvider provider = directory is null
            ? new FakeProvider(null)
            : new FakeProvider(directory);
        return new UserTokenEpochGuard(cache, new FakeScopeFactory(provider));
    }

    private static ClaimsPrincipal User(int epoch) => new(new ClaimsIdentity(
        [
            new Claim(PlatformClaimTypes.PrincipalKind, PlatformPrincipalKinds.User),
            new Claim(PlatformClaimTypes.Subject, UserId.ToString()),
            new Claim(PlatformClaimTypes.TokenEpoch, epoch.ToString(CultureInfo.InvariantCulture)),
        ],
        "test"));

    private sealed class FakeDirectory(int? current) : IUserTokenEpochDirectory
    {
        public int Calls { get; private set; }

        public Task<int?> GetEpochAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(current);
        }
    }

    private sealed class FakeProvider(IUserTokenEpochDirectory? directory) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IUserTokenEpochDirectory) ? directory : null;
    }

    private sealed class FakeScopeFactory(IServiceProvider provider) : IServiceScopeFactory, IServiceScope
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider { get; } = provider;

        public void Dispose()
        {
        }

        public static ValueTask DisposeAsync() => default;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan span) => UtcNow += span;
    }
}
