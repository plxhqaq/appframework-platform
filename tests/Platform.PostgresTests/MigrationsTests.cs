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
        Assert.Contains(
            applied,
            static name => name.EndsWith("AddDevices", StringComparison.Ordinal));
        Assert.Contains(
            applied,
            static name => name.EndsWith("AddDocumentApplicableVersion", StringComparison.Ordinal));

        var exists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.release_packages') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(exists);

        var devicesExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.devices') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(devicesExists);

        var deviceKeyIndexExists = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'devices' AND indexname = 'ux_devices_app_device_key') AS \"Value\"")
            .SingleAsync();
        Assert.True(deviceKeyIndexExists);

        var applicableVersionExists = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'documents' AND column_name = 'applicable_version') AS \"Value\"")
            .SingleAsync();
        Assert.True(applicableVersionExists);
    }
}
