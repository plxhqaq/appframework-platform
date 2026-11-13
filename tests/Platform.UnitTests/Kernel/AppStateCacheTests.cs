using Platform.Kernel.Abstractions;
using Platform.Kernel.Security;

namespace Platform.UnitTests.Kernel;

/// <summary>
/// 应用状态缓存：请求路径靠它拦下停用应用的旧令牌，TTL 与主动失效都必须是确定行为。
/// </summary>
public sealed class AppStateCacheTests
{
    [Fact]
    public void 未写入时未命中()
    {
        var cache = new AppStateCache(new FakeClock(), TimeSpan.FromSeconds(30));

        Assert.False(cache.TryGet("demo", out _));
    }

    [Fact]
    public void 写入后在窗口内命中且过期后失效()
    {
        var clock = new FakeClock();
        var cache = new AppStateCache(clock, TimeSpan.FromSeconds(30));

        cache.Set("demo", enabled: false);
        Assert.True(cache.TryGet("demo", out var enabled));
        Assert.False(enabled);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(cache.TryGet("demo", out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet("demo", out _));
    }

    [Fact]
    public void 主动失效立即生效()
    {
        var cache = new AppStateCache(new FakeClock(), TimeSpan.FromMinutes(1));

        cache.Set("demo", enabled: true);
        cache.Invalidate("demo");

        Assert.False(cache.TryGet("demo", out _));
    }

    [Fact]
    public void 非正缓存窗口等价于关闭缓存()
    {
        var clock = new FakeClock();
        var cache = new AppStateCache(clock, TimeSpan.Zero);

        cache.Set("demo", enabled: true);

        Assert.False(cache.TryGet("demo", out _));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }
}
