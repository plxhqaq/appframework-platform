using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Documents.Domain;

namespace Platform.Modules.Documents.Services;

/// <summary>
/// 文档物理清理批循环：先修订后文档、每批 200、先删文件后删行。
/// 定时任务与「立即清理」共用，区别只在 cutoff 与 appKey 作用域。
/// </summary>
internal static class DocumentPurgeExecutor
{
    private const int BatchSize = 200;

    /// <summary>按截止时间（可选按应用）分批物理删除文档修订与文档。</summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="blobs">Blob 存储。</param>
    /// <param name="cutoff">删除时间上界（含）。</param>
    /// <param name="appKey">应用作用域；null 表示不限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已删行数与文件数。</returns>
    public static async Task<RetentionCleanupResult> PurgeAsync(
        PlatformDbContext db,
        IBlobStorage blobs,
        DateTimeOffset cutoff,
        string? appKey,
        CancellationToken cancellationToken)
    {
        var rows = 0;
        var files = 0;

        while (true)
        {
            var revisionQuery = db.Set<DocumentRevision>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                revisionQuery = revisionQuery.Where(x => x.Document.AppKey == appKey);
            }

            var revisions = await revisionQuery
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (revisions.Count == 0)
            {
                break;
            }

            foreach (var revision in revisions)
            {
                await blobs.DeleteAsync(revision.BlobPath, cancellationToken);
                files++;
            }

            db.Set<DocumentRevision>().RemoveRange(revisions);
            await db.SaveChangesAsync(cancellationToken);
            rows += revisions.Count;
        }

        while (true)
        {
            var documentQuery = db.Set<Document>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                documentQuery = documentQuery.Where(x => x.AppKey == appKey);
            }

            var documents = await documentQuery
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
                    await blobs.DeleteAsync(document.BlobPath, cancellationToken);
                    files++;
                }
            }

            db.Set<Document>().RemoveRange(documents);
            await db.SaveChangesAsync(cancellationToken);
            rows += documents.Count;
        }
    }
}
