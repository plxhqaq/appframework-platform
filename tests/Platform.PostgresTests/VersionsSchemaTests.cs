using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Versions.Domain;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>版本域的 PostgreSQL 专属契约：jsonb 列与唯一索引不能只在 InMemory 上「看起来能跑」。</summary>
public sealed class VersionsSchemaTests
{
    [PostgresFact]
    public async Task 清单列是可被查询的jsonb()
    {
        await using var db = await PostgresTestEnvironment.CreateDbContextAsync();
        var appKey = NewAppKey();
        var release = new Release
        {
            AppKey = appKey,
            Channel = new ReleaseChannel { AppKey = appKey, Key = "jsonb", Name = "jsonb" },
            Version = "1.0.0",
            Status = "draft",
        };
        var package = new ReleasePackage
        {
            AppKey = appKey,
            Release = release,
            Kind = "incremental",
            Target = "win-x64",
            Format = "zip",
            FileName = "pkg.zip",
            Sha256 = new string('a', 64),
            SizeBytes = 1,
            ManifestJson = """{"kind":"incremental","version":"1.0.0"}""",
            ManifestSource = "zip",
            Status = "pending",
        };
        db.Add(package);
        await db.SaveChangesAsync();

        var kind = await db.Database
            .SqlQueryRaw<string>(
                "SELECT manifest_json->>'kind' AS \"Value\" FROM release_packages WHERE id = {0}",
                package.Id)
            .SingleAsync();
        Assert.Equal("incremental", kind);
    }

    [PostgresFact]
    public async Task 渠道键唯一索引挡住并发重复插入()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var appKey = NewAppKey();

        async Task<bool> TryInsertAsync()
        {
            await using var db = PostgresTestEnvironment.CreateDbContext(connectionString, new TestClock());
            db.Add(new ReleaseChannel { AppKey = appKey, Key = "dup", Name = "dup" });
            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                })
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TryInsertAsync()));

        Assert.Equal(1, results.Count(static inserted => inserted));
    }

    private static string NewAppKey() => "pg" + Guid.NewGuid().ToString("N")[..10];
}
