using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Apps.Persistence;
using Platform.Modules.Backups.Persistence;
using Platform.Modules.Documents.Persistence;
using Platform.Modules.Identity.Persistence;
using Platform.Modules.Licensing.Persistence;
using Platform.Modules.Versions.Persistence;
using Platform.Modules.Webhooks.Persistence;

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
    /// <summary>
    /// 固定装配全部模块的 contributor：EF 的模型缓存按上下文类型共享，
    /// 不同测试传不同 contributor 集合会让实体是否在模型里取决于执行顺序。
    /// </summary>
    public static PlatformDbContext Create(IClock clock) =>
        new(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseInMemoryDatabase($"platform-tests-{Guid.NewGuid():N}")
                .Options,
            [new AppsModelContributor(), new IdentityModelContributor(), new VersionsModelContributor(),
             new LicensingModelContributor(), new BackupsModelContributor(), new DocumentsModelContributor(),
             new WebhooksModelContributor()],
            clock);
}
