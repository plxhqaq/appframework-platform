using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Retention;

/// <summary>
/// 版本域的保留策略与孤儿 Blob 回收：
/// 过期上传票据、超期失败包、可选归档版本，以及 <c>versions/</c> 下无人引用的内容。
/// </summary>
/// <remarks>
/// 只删数据库行的步骤不碰 Blob；内容统一交给孤儿扫描，按「无引用 + 超过宽限期」回收。
/// 扫描默认 dry-run，只记录数量与字节，生产验证一周后再切正式删除；多实例用
/// PostgreSQL 会话级 advisory lock 保证同一时刻只有一个实例在扫。
/// </remarks>
public sealed class VersionsRetentionTask : IRetentionCleanupTask
{
    /// <summary>已过期票据再保留一天，便于排障时回看。</summary>
    private static readonly TimeSpan TicketGrace = TimeSpan.FromDays(1);

    /// <summary>孤儿扫描的 advisory lock 键（全局唯一即可，取发布日期）。</summary>
    private const int AdvisoryLockKey = 20261113;

    private static readonly Action<ILogger, int, long, Exception?> SweepDryRun =
        LoggerMessage.Define<int, long>(
            LogLevel.Warning,
            new EventId(1, "VersionsBlobSweepDryRun"),
            "孤儿 Blob 扫描（dry-run）：候选 {Candidates} 个，{Bytes} 字节；未删除任何文件。");

    private static readonly Action<ILogger, int, long, Exception?> SweepDeleted =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(2, "VersionsBlobSweepDeleted"),
            "孤儿 Blob 清理完成：删除 {Files} 个文件，{Bytes} 字节。");

    private static readonly Action<ILogger, Exception?> SweepSkipped =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(3, "VersionsBlobSweepSkipped"),
            "另一个实例正在执行孤儿 Blob 扫描，本轮跳过。");

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IBlobEnumerator _enumerator;
    private readonly IClock _clock;
    private readonly VersionsOptions _options;
    private readonly PlatformMetrics _metrics;
    private readonly ILogger<VersionsRetentionTask> _logger;

    /// <summary>构造。</summary>
    public VersionsRetentionTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IBlobEnumerator enumerator,
        IClock clock,
        IOptions<VersionsOptions> options,
        PlatformMetrics metrics,
        ILogger<VersionsRetentionTask> logger)
    {
        _db = db;
        _blobs = blobs;
        _enumerator = enumerator;
        _clock = clock;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "versions";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await DeleteExpiredTicketsAsync(now, cancellationToken);
        rows += await DeleteStaleFailedPackagesAsync(now, cancellationToken);
        rows += await DeleteExpiredArchivedReleasesAsync(now, cancellationToken);

        var deletedFiles = 0;
        if (_options.BlobSweepEnabled)
        {
            deletedFiles = await SweepOrphanBlobsAsync(now, cancellationToken);
        }

        return new RetentionCleanupResult(rows, deletedFiles);
    }

    private async Task<int> DeleteExpiredTicketsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - TicketGrace;
        var deleted = 0;
        while (true)
        {
            var batch = await _db.Set<VersionUploadTicket>()
                .Where(x => x.ExpiresAt < cutoff)
                .OrderBy(static x => x.ExpiresAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            _db.Set<VersionUploadTicket>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            _metrics.Increment(
                PlatformMetricNames.VersionsUploadTicketsExpired,
                PlatformMetricNames.VersionsUploadTicketsExpiredHelp,
                by: deleted);
        }

        return deleted;
    }

    private async Task<int> DeleteStaleFailedPackagesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_options.FailedPackageRetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = now.AddDays(-_options.FailedPackageRetentionDays);
        var deleted = 0;
        while (true)
        {
            var batch = await _db.Set<ReleasePackage>()
                .Where(x => x.Status == VersionConventions.PackageStatusFailed && x.UpdatedAt < cutoff)
                .OrderBy(static x => x.UpdatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            _db.Set<ReleasePackage>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<int> DeleteExpiredArchivedReleasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_options.ArchivedReleaseRetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = now.AddDays(-_options.ArchivedReleaseRetentionDays);
        var deleted = 0;
        while (true)
        {
            var batch = await _db.Set<Release>()
                .Where(x => x.Status == "archived" && x.ArchivedAt != null && x.ArchivedAt < cutoff)
                .OrderBy(static x => x.ArchivedAt)
                .Take(BatchSize)
                .Include(static x => x.Artifacts)
                .ThenInclude(static x => x.Files)
                .Include(static x => x.Packages)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            // 行数按实际删除的实体计：版本 + 产物 + 文件 + 包。
            deleted += batch.Count
                       + batch.Sum(static x => x.Artifacts.Count)
                       + batch.Sum(static x => x.Artifacts.Sum(static a => a.Files.Count))
                       + batch.Sum(static x => x.Packages.Count);

            _db.Set<Release>().RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<int> SweepOrphanBlobsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            return await SweepCoreAsync(now, cancellationToken);
        }

        // advisory lock 是会话级的：必须在同一个物理连接上 lock → 扫描 → unlock，
        // 所以这里显式打开 DbContext 的连接并保持到扫描结束。
        var connection = _db.Database.GetDbConnection();
        var owned = connection.State != ConnectionState.Open;
        if (owned)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var acquired = false;
        try
        {
            acquired = await TryAcquireLockAsync(connection, cancellationToken);
            if (!acquired)
            {
                SweepSkipped(_logger, null);
                return 0;
            }

            return await SweepCoreAsync(now, cancellationToken);
        }
        finally
        {
            if (acquired)
            {
                await ReleaseLockAsync(connection, cancellationToken);
            }

            if (owned)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<int> SweepCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddHours(-Math.Max(0, _options.BlobSweepGraceHours));
        var batchSize = Math.Max(1, _options.BlobSweepBatchSize);
        var candidates = new List<BlobCandidate>(batchSize);
        var scanned = 0;
        var deleted = 0;
        long scannedBytes = 0;
        long deletedBytes = 0;

        await foreach (var entry in _enumerator.EnumerateAsync("versions/", cancellationToken))
        {
            if (entry.LastModifiedAt >= cutoff)
            {
                continue;
            }

            var candidate = ParseCandidate(entry);
            if (candidate is null)
            {
                continue;
            }

            candidates.Add(candidate.Value);
            if (candidates.Count >= batchSize)
            {
                var result = await ProcessBatchAsync(candidates, cancellationToken);
                scanned += result.Scanned;
                scannedBytes += result.ScannedBytes;
                deleted += result.Deleted;
                deletedBytes += result.DeletedBytes;
                candidates.Clear();
            }
        }

        if (candidates.Count > 0)
        {
            var result = await ProcessBatchAsync(candidates, cancellationToken);
            scanned += result.Scanned;
            scannedBytes += result.ScannedBytes;
            deleted += result.Deleted;
            deletedBytes += result.DeletedBytes;
        }

        if (scanned > 0)
        {
            _metrics.Increment(
                PlatformMetricNames.VersionsBlobOrphansScanned,
                PlatformMetricNames.VersionsBlobOrphansScannedHelp,
                by: scanned);
            if (_options.BlobSweepDryRun)
            {
                SweepDryRun(_logger, scanned, scannedBytes, null);
            }
        }

        if (deleted > 0)
        {
            _metrics.Increment(
                PlatformMetricNames.VersionsBlobOrphansDeleted,
                PlatformMetricNames.VersionsBlobOrphansDeletedHelp,
                by: deleted);
            _metrics.Increment(
                PlatformMetricNames.VersionsBlobOrphansDeletedBytes,
                PlatformMetricNames.VersionsBlobOrphansDeletedBytesHelp,
                by: deletedBytes);
            SweepDeleted(_logger, deleted, deletedBytes, null);
        }

        return deleted;
    }

    private async Task<SweepBatchResult> ProcessBatchAsync(
        List<BlobCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // 删除前再查一次引用：候选是按单个应用目录枚举的，但引用可能来自任何应用。
        var shas = candidates.Select(static x => x.Sha256).Distinct(StringComparer.Ordinal).ToList();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sha in await _db.Set<ReleaseFile>()
                     .Where(x => shas.Contains(x.Sha256))
                     .Select(static x => x.Sha256)
                     .Distinct()
                     .ToListAsync(cancellationToken))
        {
            referenced.Add(sha);
        }

        foreach (var sha in await _db.Set<ReleasePackage>()
                     .Where(x => shas.Contains(x.Sha256))
                     .Select(static x => x.Sha256)
                     .Distinct()
                     .ToListAsync(cancellationToken))
        {
            referenced.Add(sha);
        }

        foreach (var sha in await _db.Set<AppUpdaterRelease>()
                     .Where(x => shas.Contains(x.Sha256))
                     .Select(static x => x.Sha256)
                     .Distinct()
                     .ToListAsync(cancellationToken))
        {
            referenced.Add(sha);
        }

        var scanned = 0;
        var deleted = 0;
        long scannedBytes = 0;
        long deletedBytes = 0;

        foreach (var candidate in candidates)
        {
            if (referenced.Contains(candidate.Sha256))
            {
                continue;
            }

            scanned++;
            scannedBytes += candidate.SizeBytes;
            if (_options.BlobSweepDryRun)
            {
                continue;
            }

            await _blobs.DeleteAsync(candidate.RelativePath, cancellationToken);
            deleted++;
            deletedBytes += candidate.SizeBytes;
        }

        return new SweepBatchResult(scanned, scannedBytes, deleted, deletedBytes);
    }

    private static BlobCandidate? ParseCandidate(BlobEntry entry)
    {
        // 只认 versions/{appKey}/{sha256} 与它的下载缓存 versions/{appKey}/{sha256}.zip，
        // 其他文件不碰。缓存与源内容共用引用判定，源内容不再被引用时一起回收。
        var segments = entry.RelativePath.Split('/');
        if (segments.Length != 3 || !string.Equals(segments[0], "versions", StringComparison.Ordinal))
        {
            return null;
        }

        var sha = segments[2];
        if (sha.EndsWith(".zip", StringComparison.Ordinal))
        {
            sha = sha[..^".zip".Length];
        }

        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
        {
            return null;
        }

        return new BlobCandidate(entry.RelativePath, sha, entry.SizeBytes);
    }

    private static async Task<bool> TryAcquireLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = AdvisoryLockKey;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static async Task ReleaseLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = AdvisoryLockKey;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private const int BatchSize = 500;

    private readonly record struct BlobCandidate(string RelativePath, string Sha256, long SizeBytes);

    private readonly record struct SweepBatchResult(
        int Scanned,
        long ScannedBytes,
        int Deleted,
        long DeletedBytes);
}
