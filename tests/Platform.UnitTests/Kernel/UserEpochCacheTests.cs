using Platform.Kernel.Abstractions;
using Platform.Kernel.Security;

namespace Platform.UnitTests.Kernel;

/// <summary>用户令牌代号短缓存：命中窗口、过期移除与主动失效必须与 AppContextGuard 同口径。</summary>
public sealed class UserEpochCacheTests
{
    [Fact]
    public void 未写入时未命中()
    {
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.FromSeconds(30));

        Assert.False(cache.TryGet(Guid.NewGuid(), out _));
    }

    [Fact]
    public void 写入后窗口内命中且过期后失效()
    {
        var clock = new FakeClock();
        var cache = new UserEpochCache(clock, TimeSpan.FromSeconds(30));
        var userId = Guid.NewGuid();

        cache.Set(userId, 3);
        Assert.True(cache.TryGet(userId, out var epoch));
        Assert.Equal(3, epoch);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(cache.TryGet(userId, out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet(userId, out _));
    }

    [Fact]
    public void 主动失效立即生效()
    {
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.FromMinutes(1));
        var userId = Guid.NewGuid();

        cache.Set(userId, 7);
        cache.Invalidate(userId);

        Assert.False(cache.TryGet(userId, out _));
    }

    [Fact]
    public void 非正TTL完全不缓存()
    {
        var cache = new UserEpochCache(new FakeClock(), TimeSpan.Zero);
        var userId = Guid.NewGuid();

        cache.Set(userId, 5);

        Assert.False(cache.TryGet(userId, out _));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan span) => UtcNow += span;
    }
}
