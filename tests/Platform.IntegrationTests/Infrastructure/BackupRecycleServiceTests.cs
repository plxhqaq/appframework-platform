using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Storage;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;
using Platform.Modules.Backups.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>备份回收站：恢复先验文件、缺失拒绝；清理先文件后行；全部清理只作用于当前应用。</summary>
public sealed class BackupRecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "platform-recycle-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 恢复清删除标记且文件缺失拒绝恢复()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions()));

        var backup = await SeedAsync(db, blobs, clock, "demo", "restore", "backups/demo/restore/old.json");

        var restored = await service.RestoreAsync("demo", backup.Id);
        Assert.Equal("restore", restored.NamespaceKey);
        Assert.Null((await db.Set<Backup>().SingleAsync(x => x.Id == backup.Id)).DeletedAt);

        var row = await db.Set<Backup>().SingleAsync(x => x.Id == backup.Id);
        row.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync();
        await blobs.DeleteAsync(backup.BlobPath!);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync("demo", backup.Id));
        Assert.Equal(ApiErrorCode.BackupsBlobReclaimed, error.Code);
    }

    [Fact]
    public async Task 列表标记文件完好并给出剩余宽限()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }));

        var intact = await SeedAsync(db, blobs, clock, "demo", "list", "backups/demo/list/intact.json");
        var missing = await SeedAsync(db, blobs, clock, "demo", "list", "backups/demo/list/gone.json");
        await blobs.DeleteAsync(missing.BlobPath!);

        var page = await service.ListDeletedAsync(
            "demo", new PageQuery { Page = 1, PageSize = 20 }, keyword: null);

        Assert.Equal(2, page.Total);
        var intactRow = Assert.Single(page.Items, x => x.Id == intact.Id);
        Assert.True(intactRow.FileIntact);
        // 种子把删除时间放在 5 分钟前：宽限 30 分钟，剩余应为 25 分钟（向上取整）。
        Assert.InRange(intactRow.RemainingGraceMinutes, 24, 25);
        Assert.False(Assert.Single(page.Items, x => x.Id == missing.Id).FileIntact);
    }

    [Fact]
    public async Task 全部清理只作用于当前应用且无视宽限期()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions { CleanupGraceMinutes = 600 }));

        var mine = await SeedAsync(db, blobs, clock, "demo", "purge", "backups/demo/purge/a.json");
        var other = await SeedAsync(db, blobs, clock, "other", "purge", "backups/other/purge/b.json");

        var result = await service.PurgeAllAsync("demo");

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Null(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == mine.Id));
        Assert.NotNull(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == other.Id));
        Assert.True(await blobs.ExistsAsync(other.BlobPath!));
    }

    [Fact]
    public async Task 单条清理先删文件后删行()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions()));

        var backup = await SeedAsync(db, blobs, clock, "demo", "single", "backups/demo/single/a.json");

        var result = await service.PurgeAsync("demo", backup.Id);

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(await blobs.ExistsAsync(backup.BlobPath!));
        Assert.Null(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == backup.Id));
    }

    [Fact]
    public async Task 命名空间满额仍可恢复()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions()));

        var deleted = await SeedAsync(db, blobs, clock, "demo", "full", "backups/demo/full/deleted.json");
        var space = await db.Set<BackupNamespace>().SingleAsync(x => x.Id == deleted.NamespaceId);
        space.MaxVersions = 1;
        db.Add(new Backup
        {
            AppKey = "demo",
            NamespaceId = space.Id,
            Name = "active",
            FrameworkVersion = "1.0.0",
            BlobPath = "backups/demo/full/active.json",
            Sha256 = new string('b', 64),
            SizeBytes = 7,
        });
        await db.SaveChangesAsync();

        var restored = await service.RestoreAsync("demo", deleted.Id);

        // 恢复不受配额门禁：max_versions=1 且已有 1 条未删行，恢复后为 2 条（后续新建才会 406）。
        Assert.Equal("full", restored.NamespaceKey);
        Assert.Equal(
            2,
            await db.Set<Backup>().CountAsync(x => x.NamespaceId == space.Id && x.DeletedAt == null));
    }

    private static async Task<Backup> SeedAsync(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock,
        string appKey,
        string spaceKey,
        string blobPath)
    {
        var space = new BackupNamespace { AppKey = appKey, Key = spaceKey, Name = spaceKey };
        db.Add(space);
        var backup = new Backup
        {
            AppKey = appKey,
            NamespaceId = space.Id,
            Name = "deleted",
            FrameworkVersion = "1.0.0",
            BlobPath = blobPath,
            Sha256 = new string('a', 64),
            SizeBytes = 7,
            DeletedAt = clock.UtcNow.AddMinutes(-5),
        };
        db.Add(backup);
        await db.SaveChangesAsync();
        await blobs.WriteAsync(blobPath, new MemoryStream("payload"u8.ToArray()));
        return backup;
    }

    private LocalBlobStorage CreateBlobs() =>
        new(Options.Create(new BlobStorageOptions { RootPath = _root }));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
