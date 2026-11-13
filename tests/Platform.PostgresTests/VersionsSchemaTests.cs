using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Versions.Domain;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>版本域的 PostgreSQL 专属契约：jsonb 列与唯一索引不能只在 InMemory 上「看起来能跑」。</summary>
public sealed class VersionsSchemaTests
{
    private static readonly string[] ChannelTags = ["hardware:scanner", "win-x64"];

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
            // 摄取用例的认领是全局的（Worker 不按应用过滤），这里不能留 pending 行，
            // 否则它会被并发抽取的 Worker 抢走，导致那边的并发断言失真
            Status = "ready",
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

    [PostgresFact]
    public async Task 受众标签按text数组往返并有空默认值()
    {
        await using var db = await PostgresTestEnvironment.CreateDbContextAsync();
        var appKey = NewAppKey();
        var tagged = new ReleaseChannel
        {
            AppKey = appKey,
            Key = "audience",
            Name = "audience",
            AudienceTags = [.. ChannelTags],
            AutoCreated = true,
        };
        var plain = new ReleaseChannel { AppKey = appKey, Key = "plain", Name = "plain" };
        db.AddRange(tagged, plain);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.Set<ReleaseChannel>().AsNoTracking().SingleAsync(x => x.Id == tagged.Id);
        Assert.Equal(ChannelTags, reloaded.AudienceTags);
        Assert.True(reloaded.AutoCreated);
        Assert.Empty((await db.Set<ReleaseChannel>().AsNoTracking().SingleAsync(x => x.Id == plain.Id)).AudienceTags);

        // 列类型必须是 text[]，客户端按标签匹配靠的是数组包含而不是字符串前缀。
        var columnType = await db.Database
            .SqlQueryRaw<string>(
                "SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'release_channels' AND column_name = 'audience_tags'")
            .SingleAsync();
        Assert.Equal("ARRAY", columnType);
    }

    private static string NewAppKey() => "pg" + Guid.NewGuid().ToString("N")[..10];
}
