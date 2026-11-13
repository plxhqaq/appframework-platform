using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Packages;

namespace Platform.Modules.Versions.Services;

/// <summary>增量包异步摄取：认领待处理包、校验 zip、落文件清单、按需发布。</summary>
public interface IReleasePackageIngestionService
{
    /// <summary>把租约过期的 <c>processing</c> 行重置为 pending（超次数的置 failed），返回处理行数。</summary>
    Task<int> RecoverStaleAsync(CancellationToken cancellationToken = default);

    /// <summary>认领并处理一个包；没有可处理的包时返回 null。</summary>
    Task<ReleasePackageDto?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 摄取实现。全流程分四步：认领（PostgreSQL <c>FOR UPDATE SKIP LOCKED</c>，测试库用进程内闸门）→
/// 重新读清单并与登记时比对 → 逐文件校验后写内容寻址 Blob → 单事务落 <see cref="ReleaseArtifact"/>/
/// <see cref="ReleaseFile"/> 并置 <c>ready</c>。清单/哈希/路径类错误不重试，IO 类错误最多重试到配置次数。
/// </summary>
public sealed partial class ReleasePackageIngestionService : IReleasePackageIngestionService
{
    private const string ClaimSql = """
        UPDATE release_packages
        SET status = @processing,
            locked_until = @locked_until,
            locked_by = @locked_by,
            attempts = attempts + 1,
            updated_at = @now
        WHERE id = (
            SELECT id FROM release_packages
            WHERE status = @pending
              AND (locked_until IS NULL OR locked_until < @now)
            ORDER BY created_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
        )
        RETURNING id
        """;

    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);

    /// <summary>zip 中允许存在但不进入文件清单的元数据条目。</summary>
    private static readonly HashSet<string> MetadataEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ZipPackageManifestProbe.ManifestEntryName,
        ZipPackageManifestProbe.LegacyBuildInfoEntryName,
        ZipPackageManifestProbe.LegacyManifestEntryName,
        ZipPackageManifestProbe.UpdaterInfoEntryName,
    };

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IStorageCapacity _capacity;
    private readonly IClock _clock;
    private readonly IPlatformEventPublisher _events;
    private readonly VersionsOptions _options;
    private readonly ILogger<ReleasePackageIngestionService> _logger;
    private readonly PlatformMetrics _metrics;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>构造。</summary>
    public ReleasePackageIngestionService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IStorageCapacity capacity,
        IClock clock,
        IPlatformEventPublisher events,
        IOptions<VersionsOptions> options,
        ILogger<ReleasePackageIngestionService> logger,
        PlatformMetrics metrics)
    {
        _db = db;
        _blobs = blobs;
        _capacity = capacity;
        _clock = clock;
        _events = events;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    private int MaxAttempts => Math.Max(1, _options.IngestionMaxAttempts);

    /// <inheritdoc />
    public async Task<int> RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        if (_db.Database.IsRelational())
        {
            var maxAttempts = MaxAttempts;
            var exhausted = await _db.Set<ReleasePackage>()
                .Where(x => x.Status == VersionConventions.PackageStatusProcessing
                            && x.LockedUntil != null
                            && x.LockedUntil < now
                            && x.Attempts >= maxAttempts)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.Status, VersionConventions.PackageStatusFailed)
                        .SetProperty(
                            static x => x.ErrorCode,
                            ApiErrorCode.VersionsPackageIngestionFailed.ToString())
                        .SetProperty(static x => x.ErrorMessage, "摄取租约过期且尝试次数已用尽。")
                        .SetProperty(static x => x.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(static x => x.LockedBy, (string?)null)
                        .SetProperty(static x => x.CompletedAt, now),
                    cancellationToken);

            var reclaimed = await _db.Set<ReleasePackage>()
                .Where(x => x.Status == VersionConventions.PackageStatusProcessing
                            && x.LockedUntil != null
                            && x.LockedUntil < now
                            && x.Attempts < maxAttempts)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.Status, VersionConventions.PackageStatusPending)
                        .SetProperty(static x => x.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(static x => x.LockedBy, (string?)null),
                    cancellationToken);

            var total = exhausted + reclaimed;
            if (total > 0)
            {
                LogRecovered(_logger, total);
            }

            return total;
        }

        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var stale = await _db.Set<ReleasePackage>()
                .Where(x => x.Status == VersionConventions.PackageStatusProcessing
                            && x.LockedUntil != null
                            && x.LockedUntil < now)
                .ToListAsync(cancellationToken);
            foreach (var package in stale)
            {
                if (package.Attempts >= MaxAttempts)
                {
                    package.Status = VersionConventions.PackageStatusFailed;
                    package.ErrorCode = ApiErrorCode.VersionsPackageIngestionFailed.ToString();
                    package.ErrorMessage = "摄取租约过期且尝试次数已用尽。";
                    package.CompletedAt = now;
                }
                else
                {
                    package.Status = VersionConventions.PackageStatusPending;
                }

                package.LockedUntil = null;
                package.LockedBy = null;
            }

            if (stale.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                LogRecovered(_logger, stale.Count);
            }

            return stale.Count;
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReleasePackageDto?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var lockedUntil = now.AddMinutes(Math.Max(1, _options.IngestionLockMinutes));
        var claimedId = await ClaimNextAsync(now, lockedUntil, cancellationToken);
        if (claimedId is null)
        {
            return null;
        }

        _db.ChangeTracker.Clear();
        var package = await _db.Set<ReleasePackage>()
            .Include(static x => x.Release)
            .ThenInclude(static x => x.Channel)
            .Include(static x => x.Release)
            .ThenInclude(static x => x.Artifacts)
            .ThenInclude(static x => x.Files)
            .FirstAsync(x => x.Id == claimedId.Value, cancellationToken);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var publishedNow = await IngestAsync(package, cancellationToken);
            RecordIngest("ready", Stopwatch.GetElapsedTime(started).TotalSeconds, package.FileCount);
            if (publishedNow)
            {
                await PublishReleasedAsync(package, cancellationToken);
            }

            LogIngested(_logger, package.Id, package.Release.AppKey, package.Release.Version, package.FileCount ?? 0);
            return ReleasePackageService.ToDto(
                package, package.Release.Channel.Key, package.Release.Version, reused: false);
        }
        catch (IngestionFailure failure)
        {
            RecordIngest(
                failure.Retryable ? "retry" : "failed",
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                null);
            await MarkFailureAsync(package.Id, failure, cancellationToken);
            return await ReloadDtoAsync(package.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordIngest("canceled", Stopwatch.GetElapsedTime(started).TotalSeconds, null);
            await ReleaseClaimAsync(package.Id, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            RecordIngest("failed", Stopwatch.GetElapsedTime(started).TotalSeconds, null);
            LogUnexpected(_logger, package.Id, exception);
            await MarkFailureAsync(
                package.Id,
                new IngestionFailure(
                    ApiErrorCode.VersionsPackageIngestionFailed,
                    $"摄取异常：{exception.Message}",
                    retryable: true),
                CancellationToken.None);
            return await ReloadDtoAsync(package.Id, CancellationToken.None);
        }
    }

    private async Task<Guid?> ClaimNextAsync(
        DateTimeOffset now,
        DateTimeOffset lockedUntil,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            return await ClaimRelationalAsync(now, lockedUntil, cancellationToken);
        }

        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var candidate = await _db.Set<ReleasePackage>()
                .Where(x => x.Status == VersionConventions.PackageStatusPending
                            && (x.LockedUntil == null || x.LockedUntil < now))
                .OrderBy(static x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
            {
                return null;
            }

            MarkClaimed(candidate, lockedUntil);
            await _db.SaveChangesAsync(cancellationToken);
            return candidate.Id;
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private void MarkClaimed(ReleasePackage package, DateTimeOffset lockedUntil)
    {
        package.Status = VersionConventions.PackageStatusProcessing;
        package.LockedUntil = lockedUntil;
        package.LockedBy = _workerId;
        package.Attempts++;
    }

    private async Task<Guid?> ClaimRelationalAsync(
        DateTimeOffset now,
        DateTimeOffset lockedUntil,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            AddParameter(command, "processing", VersionConventions.PackageStatusProcessing);
            AddParameter(command, "pending", VersionConventions.PackageStatusPending);
            AddParameter(command, "locked_until", lockedUntil);
            AddParameter(command, "locked_by", _workerId);
            AddParameter(command, "now", now);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result switch
            {
                Guid id => id,
                string text when Guid.TryParse(text, out var parsed) => parsed,
                _ => null,
            };
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task<bool> IngestAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        if (package.Kind != VersionConventions.PackageKindIncremental)
        {
            // 只有增量包需要摄取；全量包在登记时已是 ready，这里按幂等成功处理。
            package.Status = VersionConventions.PackageStatusReady;
            package.CompletedAt = _clock.UtcNow;
            package.LockedUntil = null;
            package.LockedBy = null;
            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (string.IsNullOrWhiteSpace(package.ManifestJson) || string.IsNullOrWhiteSpace(package.ManifestHash))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestMissing,
                "增量包缺少登记时的规范化清单，无法摄取。",
                retryable: false);
        }

        if (!PackageManifest.TryParseJson(
                package.ManifestJson,
                package.ManifestSource,
                out var manifest,
                out var manifestError)
            || manifest is null)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestInvalid,
                manifestError ?? "登记的清单无法解析。",
                retryable: false);
        }

        if (manifest.Kind != VersionConventions.PackageKindIncremental || manifest.Files.Count == 0)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestInvalid,
                "增量包清单必须包含非空 files。",
                retryable: false);
        }

        if (manifest.AppKey is { Length: > 0 } manifestAppKey
            && !string.Equals(manifestAppKey, package.AppKey, StringComparison.Ordinal))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageAppMismatch,
                $"清单属于应用 {manifestAppKey}，不能摄取到 {package.AppKey}。",
                retryable: false);
        }

        if (!string.Equals(manifest.Version, package.Release.Version, StringComparison.Ordinal))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单版本 {manifest.Version} 与版本 {package.Release.Version} 不一致。",
                retryable: false);
        }

        if (manifest.RuntimeIdentifier is { Length: > 0 } manifestRuntime
            && !string.Equals(manifestRuntime, package.Target, StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单运行时 {manifestRuntime} 与包目标 {package.Target} 不一致。",
                retryable: false);
        }

        if (manifest.Files.Count > _options.MaxFilesPerArtifact)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageUnsafeArchive,
                $"文件数 {manifest.Files.Count} 超过上限 {_options.MaxFilesPerArtifact}。",
                retryable: false);
        }

        long declaredTotal = 0;
        foreach (var file in manifest.Files)
        {
            declaredTotal = checked(declaredTotal + file.SizeBytes);
        }

        if (declaredTotal > _options.MaxExtractedBytesPerPackage)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageUnsafeArchive,
                $"清单解压总量 {declaredTotal} 超过上限 {_options.MaxExtractedBytesPerPackage}。",
                retryable: false);
        }

        // 解压前先看磁盘：不足直接失败，不产生解压到一半的半成品。
        var available = await _capacity.GetAvailableFreeBytesAsync(cancellationToken);
        var required = declaredTotal + Math.Max(0, _options.MinFreeDiskBytes);
        if (available < required)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsStorageInsufficient,
                $"剩余磁盘 {available} 字节，不足以解压清单总量 {declaredTotal} 并保留 {_options.MinFreeDiskBytes} 字节余量。",
                retryable: false);
        }

        var packageBlobPath = VersionBlobPaths.Blob(package.AppKey, package.Sha256);
        if (!await _blobs.ExistsAsync(packageBlobPath, cancellationToken))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageIngestionFailed,
                "包 Blob 不存在（可能已被清理），请重新上传。",
                retryable: false);
        }

        var packageLength = await _blobs.GetLengthAsync(packageBlobPath, cancellationToken);
        if (packageLength != package.SizeBytes)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageHashMismatch,
                $"包 Blob 长度 {packageLength} 与登记长度 {package.SizeBytes} 不一致。",
                retryable: false);
        }

        await using var stream = await _blobs.OpenReadAsync(packageBlobPath, cancellationToken);
        if (!stream.CanSeek)
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestInvalid,
                "包 Blob 流不可随机读取，无法解析 zip。",
                retryable: false);
        }

        var reprobe = ZipPackageManifestProbe.TryRead(stream, _options);
        if (!reprobe.Success)
        {
            throw new IngestionFailure(
                reprobe.ErrorCode ?? ApiErrorCode.VersionsPackageManifestInvalid,
                reprobe.ErrorMessage ?? "重新读取包内清单失败。",
                retryable: false);
        }

        if (!string.Equals(reprobe.Manifest!.ManifestHash, package.ManifestHash, StringComparison.Ordinal))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestConflict,
                "包内清单与登记时不一致（Blob 可能被替换）。",
                retryable: false);
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var safety = PackageArchiveSafety.Validate(archive, _options);
        if (!safety.IsSafe)
        {
            throw new IngestionFailure(
                safety.ErrorCode ?? ApiErrorCode.VersionsPackageUnsafeArchive,
                safety.ErrorMessage ?? "zip 安全校验未通过。",
                retryable: false);
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var path = PackagePaths.Normalize(entry.FullName);
            if (path is null)
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsPackageUnsafeArchive,
                    $"zip 条目路径非法：{entry.FullName}。",
                    retryable: false);
            }

            if (!entries.TryAdd(path, entry))
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsPackageUnsafeArchive,
                    $"zip 条目路径重复（大小写不敏感）：{path}。",
                    retryable: false);
            }
        }

        var manifestPaths = new HashSet<string>(
            manifest.Files.Select(static x => x.Path),
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in entries.Keys)
        {
            if (!manifestPaths.Contains(path) && !MetadataEntries.Contains(path))
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsPackageIngestionFailed,
                    $"包内存在清单外的文件：{path}。",
                    retryable: false);
            }
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry))
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsPackageIngestionFailed,
                    $"包内缺少清单文件：{file.Path}。",
                    retryable: false);
            }

            if (entry.Length != file.SizeBytes)
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsPackageHashMismatch,
                    $"文件 {file.Path} 的长度 {entry.Length} 与清单 {file.SizeBytes} 不一致。",
                    retryable: false);
            }

            await EnsureFileBlobAsync(package.AppKey, entry, file, cancellationToken);
        }

        return await PersistAsync(package, manifest, cancellationToken);
    }

    private async Task EnsureFileBlobAsync(
        string appKey,
        ZipArchiveEntry entry,
        ReleaseFileDto file,
        CancellationToken cancellationToken)
    {
        var blobPath = VersionBlobPaths.Blob(appKey, file.Sha256);
        var existed = await _blobs.ExistsAsync(blobPath, cancellationToken);
        if (existed && await _blobs.GetLengthAsync(blobPath, cancellationToken) == file.SizeBytes)
        {
            return;
        }

        BlobWriteResult written;
        try
        {
            await using var content = entry.Open();
            written = await _blobs.WriteAsync(blobPath, content, cancellationToken);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw new IngestionFailure(
                ApiErrorCode.VersionsStorageInsufficient,
                $"写入文件 {file.Path} 时磁盘空间不足。",
                retryable: true);
        }

        if (written.SizeBytes == file.SizeBytes
            && string.Equals(written.Sha256, file.Sha256, StringComparison.Ordinal))
        {
            return;
        }

        if (!existed)
        {
            await _blobs.DeleteAsync(blobPath, cancellationToken);
        }

        throw new IngestionFailure(
            ApiErrorCode.VersionsPackageHashMismatch,
            $"文件 {file.Path} 的内容哈希或长度与清单不符。",
            retryable: false);
    }

    private async Task<bool> PersistAsync(
        ReleasePackage package,
        PackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var manifestHash = manifest.ManifestHash;
        var totalSize = manifest.Files.Sum(static x => x.SizeBytes);
        var publishedNow = false;

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var artifact = package.Release.Artifacts
                .FirstOrDefault(x => x.RuntimeIdentifier == package.Target);
            if (artifact is null)
            {
                artifact = new ReleaseArtifact
                {
                    Release = package.Release,
                    RuntimeIdentifier = package.Target,
                };
                package.Release.Artifacts.Add(artifact);
                // 主键由初始化器预生成；只挂到导航上会被 EF 当成 Modified 而不是 Added。
                _db.Set<ReleaseArtifact>().Add(artifact);
            }
            else if (package.Release.Status == VersionConventions.StatusPublished
                     && !string.Equals(artifact.ManifestHash, manifestHash, StringComparison.Ordinal))
            {
                throw new IngestionFailure(
                    ApiErrorCode.VersionsReleaseImmutable,
                    $"版本 {package.Release.Version} 已发布，增量包不能修改 {package.Target} 的文件清单。",
                    retryable: false);
            }

            SyncFiles(artifact, manifest.Files);
            artifact.ManifestHash = manifestHash;
            artifact.FileCount = manifest.Files.Count;
            artifact.TotalSizeBytes = totalSize;

            package.Status = VersionConventions.PackageStatusReady;
            package.CompletedAt = now;
            package.FileCount = manifest.Files.Count;
            package.ErrorCode = null;
            package.ErrorMessage = null;
            package.LockedUntil = null;
            package.LockedBy = null;

            if (package.PublishRequested
                && package.Release.Status == VersionConventions.StatusDraft)
            {
                package.Release.Status = VersionConventions.StatusPublished;
                package.Release.PublishedAt = now;
                publishedNow = true;
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new IngestionFailure(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"写入文件清单冲突：{exception.GetBaseException().Message}",
                retryable: false);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return publishedNow;
    }

    private void SyncFiles(ReleaseArtifact artifact, IReadOnlyList<ReleaseFileDto> files)
    {
        var existing = artifact.Files.ToDictionary(static x => x.Path, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            wanted.Add(file.Path);
            if (existing.TryGetValue(file.Path, out var row))
            {
                row.Path = file.Path;
                row.Sha256 = file.Sha256;
                row.SizeBytes = file.SizeBytes;
                continue;
            }

            var created = new ReleaseFile
            {
                Path = file.Path,
                Sha256 = file.Sha256,
                SizeBytes = file.SizeBytes,
            };
            artifact.Files.Add(created);
            _db.Set<ReleaseFile>().Add(created);
        }

        foreach (var stale in artifact.Files.Where(x => !wanted.Contains(x.Path)).ToList())
        {
            artifact.Files.Remove(stale);
            _db.Set<ReleaseFile>().Remove(stale);
        }
    }

    private async Task MarkFailureAsync(
        Guid packageId,
        IngestionFailure failure,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var package = await _db.Set<ReleasePackage>()
            .FirstOrDefaultAsync(x => x.Id == packageId, cancellationToken);
        if (package is null)
        {
            return;
        }

        var retry = failure.Retryable && package.Attempts < MaxAttempts;
        package.Status = retry
            ? VersionConventions.PackageStatusPending
            : VersionConventions.PackageStatusFailed;
        package.ErrorCode = failure.Code.ToString();
        package.ErrorMessage = failure.Message.Length <= 2000
            ? failure.Message
            : failure.Message[..2000];
        package.LockedUntil = null;
        package.LockedBy = null;
        package.CompletedAt = retry ? null : _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (retry)
        {
            LogRetryScheduled(_logger, packageId, package.Attempts, failure.Code, failure.Message);
        }
        else
        {
            LogFailed(_logger, packageId, failure.Code, failure.Message);
        }
    }

    private async Task ReleaseClaimAsync(Guid packageId, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var package = await _db.Set<ReleasePackage>()
            .FirstOrDefaultAsync(x => x.Id == packageId, cancellationToken);
        if (package is null)
        {
            return;
        }

        package.Status = package.Attempts >= MaxAttempts
            ? VersionConventions.PackageStatusFailed
            : VersionConventions.PackageStatusPending;
        package.ErrorCode = null;
        package.ErrorMessage = null;
        package.LockedUntil = null;
        package.LockedBy = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReleasePackageDto> ReloadDtoAsync(Guid packageId, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var package = await _db.Set<ReleasePackage>()
            .Include(static x => x.Release)
            .ThenInclude(static x => x.Channel)
            .FirstAsync(x => x.Id == packageId, cancellationToken);
        return ReleasePackageService.ToDto(
            package, package.Release.Channel.Key, package.Release.Version, reused: false);
    }

    private Task PublishReleasedAsync(ReleasePackage package, CancellationToken cancellationToken) =>
        _events.PublishAsync(
            new PlatformEvent(
                WebhookConventions.EventVersionReleased,
                package.AppKey,
                new
                {
                    releaseId = package.Release.Id,
                    channelKey = package.Release.Channel.Key,
                    version = package.Release.Version,
                    publishedAt = package.Release.PublishedAt,
                },
                _clock.UtcNow),
            cancellationToken);

    private void RecordIngest(string result, double seconds, int? fileCount)
    {
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageIngest,
            PlatformMetricNames.VersionsPackageIngestHelp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["result"] = result });
        _metrics.Observe(
            PlatformMetricNames.VersionsPackageIngestSeconds,
            PlatformMetricNames.VersionsPackageIngestSecondsHelp,
            seconds);

        if (result == "ready" && fileCount is not null)
        {
            _metrics.Set(
                PlatformMetricNames.VersionsPackageFiles,
                PlatformMetricNames.VersionsPackageFilesHelp,
                fileCount.Value,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = VersionConventions.PackageKindIncremental,
                });
        }
    }

    private static bool IsDiskFull(IOException exception)
    {
        // Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL，Unix ENOSPC。
        var code = exception.HResult & 0xFFFF;
        return code is 39 or 112 or 28;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "增量包摄取完成：{PackageId}（{AppKey}/{Version}，{FileCount} 个文件）。")]
    private static partial void LogIngested(
        ILogger logger, Guid packageId, string appKey, string version, int fileCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "增量包 {PackageId} 摄取失败，第 {Attempts} 次：{ErrorCode} {Message}。")]
    private static partial void LogRetryScheduled(
        ILogger logger, Guid packageId, int attempts, ApiErrorCode errorCode, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "增量包 {PackageId} 摄取失败：{ErrorCode} {Message}。")]
    private static partial void LogFailed(
        ILogger logger, Guid packageId, ApiErrorCode errorCode, string message);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "增量包 {PackageId} 摄取出现未预期异常。")]
    private static partial void LogUnexpected(ILogger logger, Guid packageId, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "重置 {Count} 个租约过期的摄取任务。")]
    private static partial void LogRecovered(ILogger logger, int count);

    private sealed class IngestionFailure(ApiErrorCode code, string message, bool retryable) : Exception(message)
    {
        public ApiErrorCode Code { get; } = code;

        public bool Retryable { get; } = retryable;
    }
}
