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
        var releaseId = Guid.Empty;

        await using (var seed = await PostgresTestEnvironment.CreateDbContextAsync())
        {
            var release = new Release
            {
                AppKey = appKey,
                Channel = new ReleaseChannel { AppKey = appKey, Key = "claim", Name = "claim" },
                Version = "1.0.0",
                Status = "draft",
            };
            seed.Add(release);
            for (var i = 0; i < Workers; i++)
            {
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
            }

            await seed.SaveChangesAsync();
            releaseId = release.Id;
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
        var attempts = await verify.Set<ReleasePackage>()
            .Where(x => x.ReleaseId == releaseId)
            .Select(static x => x.Attempts)
            .ToListAsync();
        Assert.All(attempts, static value => Assert.Equal(1, value));
    }
}
