using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;

namespace Platform.IntegrationTests.Support;

/// <summary>可拨动的时钟：过期类逻辑必须能精确推进时间，否则只能靠 Thread.Sleep 测。</summary>
internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

/// <summary>每次调用独立库名，避免测试之间互相污染。</summary>
internal static class TestDbContext
{
    public static PlatformDbContext Create(IClock clock) =>
        new(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseInMemoryDatabase($"platform-tests-{Guid.NewGuid():N}")
                .Options,
            [],
            clock);
}
