using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Storage;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;
using Platform.Modules.Documents.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>文档回收站：文档与修订一起恢复；任一文件缺失整体拒绝；清理按应用隔离。</summary>
public sealed class DocumentRecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "platform-doc-recycle-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 恢复同时清文档与全部修订的删除标记()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (document, revisions) = await SeedAsync(db, blobs, clock, "demo", "manual", 2);

        var restored = await service.RestoreAsync("demo", document.Id);
        Assert.Equal("manual", restored.CollectionKey);

        var row = await db.Set<Document>().Include(x => x.Revisions).SingleAsync(x => x.Id == document.Id);
        Assert.Null(row.DeletedAt);
        Assert.All(row.Revisions, revision => Assert.Null(revision.DeletedAt));
        Assert.Equal(revisions.Count, row.Revisions.Count);
    }

    [Fact]
    public async Task 任一修订文件缺失即拒绝恢复()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (document, revisions) = await SeedAsync(db, blobs, clock, "demo", "manual", 2);
        await blobs.DeleteAsync(revisions.Single(x => x.Revision == 1).BlobPath);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync("demo", document.Id));
        Assert.Equal(ApiErrorCode.DocumentsFileReclaimed, error.Code);
    }

    [Fact]
    public async Task 全部清理只作用于当前应用()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (mine, _) = await SeedAsync(db, blobs, clock, "demo", "mine", 1);
        var (other, _) = await SeedAsync(db, blobs, clock, "other", "other", 1);

        var result = await service.PurgeAllAsync("demo");

        Assert.Equal(2, result.RowsDeleted);
        Assert.Null(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == mine.Id));
        Assert.NotNull(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == other.Id));
    }

    private static DocumentRecycleService CreateService(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock) =>
        new(
            db,
            blobs,
            clock,
            new TestRetentionSettingsStore(),
            Options.Create(new DocumentsOptions()),
            new DocumentChangeNotifier([], NullLogger<DocumentChangeNotifier>.Instance));

    private static async Task<(Document Document, List<DocumentRevision> Revisions)> SeedAsync(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock,
        string appKey,
        string collectionKey,
        int revisionCount)
    {
        var collection = new DocumentCollection { AppKey = appKey, Key = collectionKey, Name = collectionKey };
        db.Add(collection);

        var revisions = new List<DocumentRevision>();
        for (var revision = 1; revision <= revisionCount; revision++)
        {
            revisions.Add(new DocumentRevision
            {
                Revision = revision,
                FileName = $"{revision}.txt",
                BlobPath = $"documents/{appKey}/{collectionKey}/r{revision}.bin",
                Sha256 = new string((char)('a' + revision), 64),
                DeletedAt = clock.UtcNow.AddMinutes(-5),
            });
        }

        var latest = revisions[^1];
        var document = new Document
        {
            AppKey = appKey,
            CollectionId = collection.Id,
            Title = "回收文档",
            Revision = revisionCount,
            FileName = latest.FileName,
            BlobPath = latest.BlobPath,
            Sha256 = latest.Sha256,
            SizeBytes = 7,
            DeletedAt = clock.UtcNow.AddMinutes(-5),
            Revisions = revisions,
        };
        db.Add(document);
        await db.SaveChangesAsync();

        foreach (var revision in revisions)
        {
            await blobs.WriteAsync(revision.BlobPath, new MemoryStream("payload"u8.ToArray()));
        }

        return (document, revisions);
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
