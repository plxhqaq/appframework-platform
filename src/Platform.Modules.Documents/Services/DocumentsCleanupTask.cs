using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>
/// 文档软删除清理：超过宽限期后先删修订文件、再删修订行，最后删文档行。
/// 文件删不掉就保留行，下轮重试，避免出现"文件还在、行没了"的泄漏。
/// </summary>
public sealed class DocumentsCleanupTask : IRetentionCleanupTask
{
    private const int BatchSize = 200;

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly DocumentsOptions _options;

    /// <summary>构造。</summary>
    public DocumentsCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<DocumentsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public string Name => "documents";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, _options.CleanupGraceMinutes));
        var rows = 0;
        var files = 0;

        while (true)
        {
            var revisions = await _db.Set<DocumentRevision>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff)
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (revisions.Count == 0)
            {
                break;
            }

            foreach (var revision in revisions)
            {
                await _blobs.DeleteAsync(revision.BlobPath, cancellationToken);
                files++;
            }

            _db.Set<DocumentRevision>().RemoveRange(revisions);
            await _db.SaveChangesAsync(cancellationToken);
            rows += revisions.Count;
        }

        while (true)
        {
            var documents = await _db.Set<Document>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff)
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (documents.Count == 0)
            {
                return new RetentionCleanupResult(rows, files);
            }

            foreach (var document in documents)
            {
                if (!string.IsNullOrEmpty(document.BlobPath))
                {
                    await _blobs.DeleteAsync(document.BlobPath, cancellationToken);
                    files++;
                }
            }

            _db.Set<Document>().RemoveRange(documents);
            await _db.SaveChangesAsync(cancellationToken);
            rows += documents.Count;
        }
    }
}
