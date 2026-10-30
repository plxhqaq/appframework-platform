using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>
/// 备份软删除清理：超过宽限期后先删 Blob、再删行；文件删不掉就保留行下轮重试。
/// </summary>
public sealed class BackupCleanupTask : IRetentionCleanupTask
{
    private const int BatchSize = 200;

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly BackupsOptions _options;

    /// <summary>构造。</summary>
    public BackupCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<BackupsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public string Name => "backups";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, _options.CleanupGraceMinutes));
        var rows = 0;
        var files = 0;

        while (true)
        {
            var batch = await _db.Set<Backup>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff)
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
                    await _blobs.DeleteAsync(backup.BlobPath, cancellationToken);
                    files++;
                }
            }

            _db.Set<Backup>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            rows += batch.Count;
        }
    }
}
