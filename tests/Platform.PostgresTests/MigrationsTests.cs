using Microsoft.EntityFrameworkCore;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>迁移是线上唯一的建库路径，必须在真 PostgreSQL 上从空库一次跑通。</summary>
public sealed class MigrationsTests
{
    [PostgresFact]
    public async Task 全部迁移能在空库落地并建出版本表()
    {
        await using var db = await PostgresTestEnvironment.CreateDbContextAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
        Assert.Contains(
            applied,
            static name => name.EndsWith("AddReleasePackageIngestion", StringComparison.Ordinal));

        var exists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.release_packages') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(exists);
    }
}
