using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Storage;
using Platform.IntegrationTests.Support;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;
using Platform.Modules.Backups.Services;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;
using Platform.Modules.Documents.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>软删除保留策略：宽限期外先删文件再删行；宽限期内不动。</summary>
public sealed class RetentionCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "platform-retention-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 备份清理先删文件再删行()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var space = new BackupNamespace { AppKey = "demo", Key = "cleanup", Name = "清理" };
        db.Add(space);
        var backup = new Backup
        {
            AppKey = "demo",
            NamespaceId = space.Id,
            Name = "old",
            FrameworkVersion = "1.0.0",
            BlobPath = "backups/demo/cleanup/old.json",
            DeletedAt = clock.UtcNow.AddHours(-2),
        };
        db.Add(backup);
        await db.SaveChangesAsync();
        await blobs.WriteAsync(backup.BlobPath, new MemoryStream("payload"u8.ToArray()));

        var settings = new TestRetentionSettingsStore();
        var task = new BackupCleanupTask(
            db, blobs, clock, Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }), settings);
        var result = await task.RunAsync();

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(await blobs.ExistsAsync(backup.BlobPath));
        Assert.Empty(await db.Set<Backup>().ToListAsync());
    }

    [Fact]
    public async Task 文档清理覆盖全部修订且宽限期内保留()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var collection = new DocumentCollection { AppKey = "demo", Key = "cleanup", Name = "清理" };
        db.Add(collection);
        var document = new Document
        {
            AppKey = "demo",
            CollectionId = collection.Id,
            Title = "旧文档",
            Revision = 2,
            FileName = "old.txt",
            BlobPath = "documents/demo/cleanup/doc/r2.bin",
            Sha256 = new string('a', 64),
            DeletedAt = clock.UtcNow.AddHours(-2),
        };
        db.Add(document);
        var fresh = new Document
        {
            AppKey = "demo",
            CollectionId = collection.Id,
            Title = "刚删的",
            Revision = 1,
            FileName = "fresh.txt",
            BlobPath = "documents/demo/cleanup/fresh/r1.bin",
            Sha256 = new string('b', 64),
            DeletedAt = clock.UtcNow.AddMinutes(-5),
        };
        db.Add(fresh);

        var deletions = new[]
        {
            (document.Id, 1, "documents/demo/cleanup/doc/r1.bin"),
            (document.Id, 2, "documents/demo/cleanup/doc/r2.bin"),
            (fresh.Id, 1, "documents/demo/cleanup/fresh/r1.bin"),
        };
        foreach (var (documentId, revision, path) in deletions)
        {
            db.Add(new DocumentRevision
            {
                DocumentId = documentId,
                Revision = revision,
                FileName = $"{revision}.txt",
                BlobPath = path,
                Sha256 = new string('c', 64),
                DeletedAt = documentId == document.Id
                    ? clock.UtcNow.AddHours(-2)
                    : clock.UtcNow.AddMinutes(-5),
            });
        }

        await db.SaveChangesAsync();
        foreach (var (_, _, path) in deletions)
        {
            await blobs.WriteAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(path)));
        }

        var settings = new TestRetentionSettingsStore();
        var task = new DocumentsCleanupTask(
            db, blobs, clock, Options.Create(new DocumentsOptions { CleanupGraceMinutes = 30 }), settings);
        var result = await task.RunAsync();

        // 只清理超过宽限期的文档：2 条修订 + 1 条文档行；最新修订文件与文档行同路径只删一次也不报错
        Assert.Equal(3, result.RowsDeleted);
        Assert.False(await blobs.ExistsAsync("documents/demo/cleanup/doc/r1.bin"));
        Assert.False(await blobs.ExistsAsync("documents/demo/cleanup/doc/r2.bin"));
        Assert.True(await blobs.ExistsAsync("documents/demo/cleanup/fresh/r1.bin"));
        Assert.NotNull(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == fresh.Id));
        Assert.Null(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == document.Id));
    }

    [Fact]
    public async Task 库内宽限期覆盖Options默认()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var space = new BackupNamespace { AppKey = "demo", Key = "grace-cover", Name = "覆盖" };
        db.Add(space);
        var backup = new Backup
        {
            AppKey = "demo",
            NamespaceId = space.Id,
            Name = "kept",
            FrameworkVersion = "1.0.0",
            BlobPath = "backups/demo/grace-cover/kept.json",
            DeletedAt = clock.UtcNow.AddMinutes(-90),
        };
        db.Add(backup);
        await db.SaveChangesAsync();
        await blobs.WriteAsync(backup.BlobPath, new MemoryStream("payload"u8.ToArray()));

        var settings = new TestRetentionSettingsStore();
        settings.Set(PlatformModuleKeys.Backups, 120);
        var task = new BackupCleanupTask(
            db, blobs, clock, Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }), settings);

        var result = await task.RunAsync();

        Assert.Equal(0, result.RowsDeleted);
        Assert.NotNull(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == backup.Id));
        Assert.True(await blobs.ExistsAsync(backup.BlobPath));
    }

    [Fact]
    public async Task 库内无配置回落Options默认()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var collection = new DocumentCollection { AppKey = "demo", Key = "grace-fallback", Name = "回落" };
        db.Add(collection);
        var document = new Document
        {
            AppKey = "demo",
            CollectionId = collection.Id,
            Title = "旧文档",
            Revision = 1,
            FileName = "old.txt",
            BlobPath = "documents/demo/grace-fallback/doc/r1.bin",
            Sha256 = new string('a', 64),
            DeletedAt = clock.UtcNow.AddMinutes(-60),
        };
        db.Add(document);
        db.Add(new DocumentRevision
        {
            DocumentId = document.Id,
            Revision = 1,
            FileName = "old.txt",
            BlobPath = document.BlobPath,
            Sha256 = new string('a', 64),
            DeletedAt = document.DeletedAt,
        });
        await db.SaveChangesAsync();
        await blobs.WriteAsync(document.BlobPath, new MemoryStream("payload"u8.ToArray()));

        var settings = new TestRetentionSettingsStore();
        var task = new DocumentsCleanupTask(
            db, blobs, clock, Options.Create(new DocumentsOptions { CleanupGraceMinutes = 30 }), settings);

        var result = await task.RunAsync();

        Assert.Equal(2, result.RowsDeleted);
        Assert.Null(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == document.Id));
    }

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

    private LocalBlobStorage CreateBlobs() =>
        new(Options.Create(new BlobStorageOptions { RootPath = _root }));
}
