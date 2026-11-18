using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Services;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>多副本同时摄取时，认领必须靠 <c>FOR UPDATE SKIP LOCKED</c> 互斥而不是靠进程内锁。</summary>
public sealed class IngestionClaimTests
{
    private const int Workers = 16;

    [PostgresFact]
    public async Task 十六个副本并发认领只会各取一个不同包()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var appKey = "pg" + Guid.NewGuid().ToString("N")[..10];
        var releaseIds = new List<Guid>();

        await using (var seed = await PostgresTestEnvironment.CreateDbContextAsync())
        {
            for (var i = 0; i < Workers; i++)
            {
                // 每个包给一个独立版本，避免同一版本的多个 pending 包被并发用例互相干扰
                var release = new Release
                {
                    AppKey = appKey,
                    Channel = new ReleaseChannel { AppKey = appKey, Key = $"claim{i}", Name = $"claim{i}" },
                    Version = "1.0.0",
                    Status = "draft",
                };
                seed.Add(release);
                seed.Add(new ReleasePackage
                {
                    AppKey = appKey,
                    Release = release,
                    Kind = "incremental",
                    Target = "win-x64",
                    Format = "zip",
                    FileName = $"part-{i}.zip",
                    Sha256 = i.ToString("x2", CultureInfo.InvariantCulture).PadLeft(64, '0'),
                    SizeBytes = 1,
                    ManifestSource = "zip",
                    Status = "pending",
                });
                releaseIds.Add(release.Id);
            }

            await seed.SaveChangesAsync();
        }

        var claimed = await Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            await using var db = PostgresTestEnvironment.CreateDbContext(connectionString, new TestClock());
            var service = new ReleasePackageIngestionService(
                db,
                new UnusedBlobStorage(),
                new UnlimitedStorageCapacity(),
                new TestClock(),
                new NullPlatformEventPublisher(),
                Options.Create(new VersionsOptions()),
                NullLogger<ReleasePackageIngestionService>.Instance,
                new PlatformMetrics());
            return await service.ProcessNextAsync();
        }));

        var ids = claimed.Where(static item => item is not null).Select(static item => item!.Id).ToList();
        Assert.Equal(Workers, ids.Count);
        Assert.Equal(Workers, ids.Distinct().Count());

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, new TestClock());
        var rows = await verify.Set<ReleasePackage>()
            .Where(x => releaseIds.Contains(x.ReleaseId))
            .Select(static x => new { x.Id, x.Attempts, x.Status, x.LockedBy, x.ErrorCode })
            .ToListAsync();
        Assert.Equal(Workers, rows.Count);
        Assert.True(
            rows.All(static x => x.Attempts == 1),
            string.Join(" | ", rows.Select(static x => $"{x.Id:N} a={x.Attempts} s={x.Status} l={x.LockedBy} e={x.ErrorCode}")));
    }
}
