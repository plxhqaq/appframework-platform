using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>宽限期配置：未配置返回 null；批量写入在一次 SaveChanges 内落两域。</summary>
public sealed class RetentionSettingsStoreTests
{
    [Fact]
    public async Task 未配置返回空且批量写入后按模块读取()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new RetentionSettingsStore(db, clock);

        Assert.Null(await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));

        await store.SetGraceMinutesAsync(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [PlatformModuleKeys.Backups] = 60,
                [PlatformModuleKeys.Documents] = 120,
            },
            "admin");

        Assert.Equal(60, await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));
        Assert.Equal(120, await store.GetGraceMinutesAsync(PlatformModuleKeys.Documents));

        await store.SetGraceMinutesAsync(
            new Dictionary<string, int>(StringComparer.Ordinal) { [PlatformModuleKeys.Documents] = 30 },
            "operator");
        Assert.Equal(60, await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));
        Assert.Equal(30, await store.GetGraceMinutesAsync(PlatformModuleKeys.Documents));

        var row = await db.Set<RetentionSettingsRow>().SingleAsync(x => x.ModuleKey == PlatformModuleKeys.Documents);
        Assert.Equal("operator", row.UpdatedBy);
        Assert.Equal(clock.UtcNow, row.UpdatedAt);
    }
}
