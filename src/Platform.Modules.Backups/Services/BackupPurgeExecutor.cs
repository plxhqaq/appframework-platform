using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Backups.Domain;

namespace Platform.Modules.Backups.Services;

/// <summary>
/// 备份物理清理批循环：每批 200、先删文件后删行；文件删不掉就保留行等下一轮。
/// 定时任务与「立即清理」共用，区别只在 cutoff 与 appKey 作用域。
/// </summary>
internal static class BackupPurgeExecutor
{
    private const int BatchSize = 200;

    /// <summary>按截止时间（可选按应用）分批物理删除备份。</summary>
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
            var query = db.Set<Backup>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                query = query.Where(x => x.AppKey == appKey);
            }

            var batch = await query
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return new RetentionCleanupResult(rows, files);
            }

            foreach (var backup in batch)
            {
                if (!string.IsNullOrEmpty(backup.BlobPath))
                {
                    await blobs.DeleteAsync(backup.BlobPath, cancellationToken);
                    files++;
                }
            }

            db.Set<Backup>().RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            rows += batch.Count;
        }
    }
}
