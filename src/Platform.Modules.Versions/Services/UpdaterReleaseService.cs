using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Packages;

namespace Platform.Modules.Versions.Services;

/// <summary>更新器版本线：独立于应用版本的分发、归档与「最新」查询。</summary>
public interface IUpdaterReleaseService
{
    /// <summary>登记更新器分发包；同目标的同一版本只允许一份内容。</summary>
    Task<UpdaterInfoDto> RegisterAsync(
        string appKey,
        RegisterUpdaterRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>分页列出更新器版本。</summary>
    Task<PageResult<UpdaterInfoDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? target,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>某运行时下的最新已发布更新器；先同 RID，再回退 any。没有时返回 null。</summary>
    Task<UpdaterInfoDto?> GetLatestAsync(
        string appKey,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>归档更新器版本（幂等）。</summary>
    Task<UpdaterInfoDto> ArchiveAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>客户端下载定位：仅已发布且 Blob 存在。</summary>
    Task<UpdaterReleaseDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>更新器下载所需信息。</summary>
public sealed record UpdaterReleaseDownload(
    Guid UpdaterId,
    string Sha256,
    long SizeBytes,
    string FileName,
    string BlobPath);

/// <summary>
/// 更新器版本线实现。版本来源按顺序：清单 <c>af-package.json</c>（kind=updater）→
/// 老 <c>updater-info.json</c> → PE <c>FileVersion</c> → 请求显式指定（人工）。
/// </summary>
public sealed partial class UpdaterReleaseService : IUpdaterReleaseService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly VersionsOptions _options;
    private readonly ILogger<UpdaterReleaseService> _logger;
    private readonly PlatformMetrics _metrics;

    /// <summary>构造。</summary>
    public UpdaterReleaseService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<VersionsOptions> options,
        ILogger<UpdaterReleaseService> logger,
        PlatformMetrics metrics)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<UpdaterInfoDto> RegisterAsync(
        string appKey,
        RegisterUpdaterRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        UpdaterInfoDto dto;
        try
        {
            dto = await RegisterCoreAsync(appKey, request, actor, cancellationToken);
        }
        catch (BusinessException)
        {
            IncrementRegister("rejected");
            throw;
        }

        IncrementRegister("accepted");
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageBytes,
            PlatformMetricNames.VersionsPackageBytesHelp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = VersionConventions.PackageKindUpdater,
            },
            dto.SizeBytes);
        return dto;
    }

    private void IncrementRegister(string result) =>
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageRegister,
            PlatformMetricNames.VersionsPackageRegisterHelp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = VersionConventions.PackageKindUpdater,
                ["result"] = result,
            });

    private async Task<UpdaterInfoDto> RegisterCoreAsync(
        string appKey,
        RegisterUpdaterRequest request,
        string? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sha256 = PackagePaths.NormalizeSha256(request.Sha256);
        if (!PackagePaths.IsSha256(sha256))
        {
            throw BusinessException.Invalid("sha256 必须是 64 位小写十六进制。");
        }

        var maxSizeBytes = _options.MaxPackageSizeBytes;
        if (request.SizeBytes <= 0 || request.SizeBytes > maxSizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageTooLarge,
                $"更新器包大小必须大于 0 且不超过 {maxSizeBytes} 字节。");
        }

        var blobPath = VersionBlobPaths.Blob(appKey, sha256);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                "更新器内容尚未上传，请先经上传票据直传。");
        }

        var actualLength = await _blobs.GetLengthAsync(blobPath, cancellationToken);
        if (actualLength != request.SizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                $"更新器内容长度 {actualLength} 与声明 {request.SizeBytes} 不一致。");
        }

        var fileName = NormalizeFileName(request.FileName, sha256);
        var probe = await ProbeAsync(blobPath, fileName, cancellationToken);
        var manifest = probe.Manifest;

        if (manifest is not null)
        {
            if (manifest.Kind != VersionConventions.PackageKindUpdater)
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsPackageKindUnsupported,
                    $"更新器包清单 kind 必须是 updater（当前 {manifest.Kind}）。");
            }

            if (manifest.AppKey is { Length: > 0 } manifestAppKey
                && !string.Equals(manifestAppKey, appKey, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsPackageAppMismatch,
                    $"更新器清单属于应用 {manifestAppKey}，不能登记到 {appKey}。");
            }
        }

        var version = FirstNonEmpty(request.Version, manifest?.Version, probe.FileVersion);
        if (!VersionComparer.TryParse(version, out _))
        {
            throw BusinessException.Invalid(
                "无法确定更新器版本：清单/PE FileVersion 均未提供合法版本，请在请求里显式指定。");
        }

        var requestedTarget = NormalizeTarget(request.Target);
        var manifestTarget = NormalizeTarget(manifest?.RuntimeIdentifier);
        if (requestedTarget is { Length: > 0 } && manifestTarget is { Length: > 0 }
            && !string.Equals(requestedTarget, manifestTarget, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单运行时 {manifestTarget} 与请求 target {requestedTarget} 不一致。");
        }

        var target = FirstNonEmpty(requestedTarget, manifestTarget, VersionConventions.PackageTargetAny);
        if (!VersionConventions.IsSupportedRuntime(target)
            && !string.Equals(target, VersionConventions.PackageTargetAny, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                $"不支持的更新器 target：{target}。");
        }

        var existing = await _db.Set<AppUpdaterRelease>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Target == target && x.Version == version,
                cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
            {
                return ToDto(existing, reused: true);
            }

            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"更新器 {target}/{version} 已存在（sha256 {existing.Sha256}），与本次内容冲突。");
        }

        var now = _clock.UtcNow;
        var entity = new AppUpdaterRelease
        {
            AppKey = appKey,
            Target = target,
            Version = version,
            FileName = fileName,
            Sha256 = sha256,
            SizeBytes = actualLength,
            ManifestHash = manifest?.HasFiles == true ? manifest.ManifestHash : null,
            ManifestJson = manifest?.ToJson(),
            Status = VersionConventions.StatusPublished,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            PublishedAt = now,
            CreatedBy = actor,
        };
        _db.Set<AppUpdaterRelease>().Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"更新器 {target}/{version} 已被其他请求登记。",
                exception);
        }

        LogRegistered(_logger, appKey, target, version, entity.Id);
        return ToDto(entity, reused: false);
    }

    /// <inheritdoc />
    public async Task<PageResult<UpdaterInfoDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? target,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = _db.Set<AppUpdaterRelease>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey);
        if (!string.IsNullOrWhiteSpace(target))
        {
            var value = target.Trim();
            query = query.Where(x => x.Target == value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var value = status.Trim();
            if (!VersionConventions.IsValidStatus(value)
                || value == VersionConventions.StatusDraft)
            {
                throw BusinessException.Invalid("status 必须是 published / archived 之一。");
            }

            query = query.Where(x => x.Status == value);
        }

        return await query
            .OrderBy(x => x.Status == VersionConventions.StatusArchived ? 1 : 0)
            .ThenByDescending(static x => x.CreatedAt)
            .ThenByDescending(static x => x.Id)
            .Select(static x => new UpdaterInfoDto(
                x.Id,
                x.Target,
                x.Version,
                x.FileName,
                x.Sha256,
                x.SizeBytes,
                x.ManifestHash,
                x.Status,
                x.Notes,
                x.PublishedAt,
                x.CreatedAt,
                x.UpdatedAt))
            .ToPageResultAsync(page, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UpdaterInfoDto?> GetLatestAsync(
        string appKey,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default)
    {
        var rid = runtimeIdentifier.Trim();
        var candidates = await _db.Set<AppUpdaterRelease>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey
                        && x.Status == VersionConventions.StatusPublished
                        && (x.Target == rid || x.Target == VersionConventions.PackageTargetAny))
            .ToListAsync(cancellationToken);

        AppUpdaterRelease? latest = null;
        foreach (var candidate in candidates)
        {
            if (!VersionComparer.TryParse(candidate.Version, out _))
            {
                continue;
            }

            if (latest is null)
            {
                latest = candidate;
                continue;
            }

            var sameTarget = string.Equals(candidate.Target, rid, StringComparison.Ordinal);
            var latestSameTarget = string.Equals(latest.Target, rid, StringComparison.Ordinal);
            if (sameTarget != latestSameTarget)
            {
                if (sameTarget)
                {
                    latest = candidate;
                }

                continue;
            }

            if (ReleaseVersionOrdering.Compare(
                    candidate.Version, candidate.PublishedAt,
                    latest.Version, latest.PublishedAt) > 0)
            {
                latest = candidate;
            }
        }

        return latest is null ? null : ToDto(latest, reused: false);
    }

    /// <inheritdoc />
    public async Task<UpdaterInfoDto> ArchiveAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.Set<AppUpdaterRelease>()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.VersionsUpdaterNotFound,
                         $"更新器 {id} 不存在。");

        if (entity.Status == VersionConventions.StatusArchived)
        {
            return ToDto(entity, reused: false);
        }

        entity.Status = VersionConventions.StatusArchived;
        entity.ArchivedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(entity, reused: false);
    }

    /// <inheritdoc />
    public async Task<UpdaterReleaseDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.Set<AppUpdaterRelease>()
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw UpdaterNotFound(id);

        if (entity.Status != VersionConventions.StatusPublished)
        {
            throw UpdaterNotFound(id);
        }

        var blobPath = VersionBlobPaths.Blob(appKey, entity.Sha256);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw UpdaterNotFound(id);
        }

        return new UpdaterReleaseDownload(
            entity.Id, entity.Sha256, entity.SizeBytes, entity.FileName, blobPath);
    }

    private async Task<UpdaterProbe> ProbeAsync(
        string blobPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.OpenReadAsync(blobPath, cancellationToken);
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var probe = ZipPackageManifestProbe.TryRead(stream, _options);
            RecordProbe("zip", ProbeResultLabel(probe));
            if (probe.Success)
            {
                return new UpdaterProbe(probe.Manifest, null);
            }

            if (probe.ErrorCode is not null
                && probe.ErrorCode != ApiErrorCode.VersionsPackageManifestMissing)
            {
                throw new BusinessException(probe.ErrorCode.Value, probe.ErrorMessage ?? "更新器清单解析失败。");
            }

            return new UpdaterProbe(null, null);
        }

        var embedded = PePackageManifestProbe.TryRead(stream);
        RecordProbe(
            Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant(),
            ProbeResultLabel(embedded));
        if (embedded.Success)
        {
            return new UpdaterProbe(embedded.Manifest, null);
        }

        if (embedded.ErrorCode is not null
            && embedded.ErrorCode != ApiErrorCode.VersionsPackageManifestMissing)
        {
            throw new BusinessException(embedded.ErrorCode.Value, embedded.ErrorMessage ?? "更新器清单解析失败。");
        }

        stream.Position = 0;
        var fileVersion = PeVersionInfoReader.ReadStringValue(stream, "FileVersion");
        return new UpdaterProbe(null, fileVersion);
    }

    private static string ProbeResultLabel(PackageProbeResult result) =>
        result.Success
            ? "success"
            : result.ErrorCode == ApiErrorCode.VersionsPackageManifestMissing ? "missing" : "invalid";

    private void RecordProbe(string format, string result) =>
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageProbe,
            PlatformMetricNames.VersionsPackageProbeHelp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["format"] = format,
                ["result"] = result,
            });

    private static UpdaterInfoDto ToDto(AppUpdaterRelease entity, bool reused) =>
        new(
            entity.Id,
            entity.Target,
            entity.Version,
            entity.FileName,
            entity.Sha256,
            entity.SizeBytes,
            entity.ManifestHash,
            entity.Status,
            entity.Notes,
            entity.PublishedAt,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static BusinessException UpdaterNotFound(Guid id) =>
        BusinessException.NotFound(ApiErrorCode.VersionsUpdaterNotFound, $"更新器 {id} 不存在或不可下载。");

    private static string NormalizeFileName(string? raw, string sha256)
    {
        var name = raw?.Trim() ?? string.Empty;
        var separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        if (name.Length == 0)
        {
            name = $"updater-{sha256[..12]}.zip";
        }

        return name.Length > 256 ? name[..256] : name;
    }

    private static string? NormalizeTarget(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "登记更新器 {AppKey}/{Target}/{Version}（{UpdaterId}）。")]
    private static partial void LogRegistered(
        ILogger logger, string appKey, string target, string version, Guid updaterId);

    private sealed record UpdaterProbe(PackageManifest? Manifest, string? FileVersion);
}
