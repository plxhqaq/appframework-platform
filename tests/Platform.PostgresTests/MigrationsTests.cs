using Microsoft.EntityFrameworkCore;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>迁移是线上唯一的建库路径，必须在真 PostgreSQL 上从空库一次跑通。</summary>
public sealed class MigrationsTests
{
    [PostgresFact]
    public async Task 单基线迁移能在空库落地并建出节点与安装实例表()
    {
        await using var db = await PostgresTestEnvironment.CreateDbContextAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        var migration = Assert.Single(applied);
        Assert.EndsWith("InitialCreate", migration, StringComparison.Ordinal);

        var exists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.release_packages') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(exists);

        var nodesExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.device_nodes') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(nodesExists);

        var installationsExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.device_installations') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(installationsExists);

        var legacyDevicesExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.devices') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.False(legacyDevicesExists);

        var nodeIndexExists = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'device_nodes' AND indexname = 'ux_device_nodes_machine_code') AS \"Value\"")
            .SingleAsync();
        Assert.True(nodeIndexExists);

        var installationIndexExists = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'device_installations' AND indexname = 'ux_device_installations_app_install') AS \"Value\"")
            .SingleAsync();
        Assert.True(installationIndexExists);

        var applicableVersionExists = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'documents' AND column_name = 'applicable_version') AS \"Value\"")
            .SingleAsync();
        Assert.True(applicableVersionExists);

        foreach (var (table, column) in new[]
                 {
                     ("refresh_tokens", "install_guid"),
                     ("single_login_keys", "app_key"),
                     ("qr_login_sessions", "app_key"),
                     ("qr_login_sessions", "machine_code"),
                     ("qr_login_sessions", "install_guid"),
                 })
        {
            var columnExists = await db.Database
                .SqlQueryRaw<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {0} AND column_name = {1}) AS \"Value\"",
                    table,
                    column)
                .SingleAsync();
            Assert.True(columnExists, $"{table}.{column} 缺失");
        }

        var entitlementMachineIndex = await db.Database
            .SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'license_entitlements' AND indexname = 'ix_license_entitlements_machine_code') AS \"Value\"")
            .SingleAsync();
        Assert.True(entitlementMachineIndex);
    }
}
