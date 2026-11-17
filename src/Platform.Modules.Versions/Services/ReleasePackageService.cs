using System.Text.RegularExpressions;
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

/// <summary>发布包登记、查询、删除与下载定位。</summary>
public interface IReleasePackageService
{
    /// <summary>
    /// 登记发布包：内容必须已经过上传票据落盘；清单优先取 <see cref="RegisterReleasePackageRequest.ManifestSha256"/>
    /// （sidecar），否则从包内探测。full 包直接可用，incremental 包等待 B9 摄取。
    /// </summary>
    Task<ReleasePackageDto> RegisterAsync(
        string appKey,
        RegisterReleasePackageRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>包详情（管理端，含摄取状态与失败原因）。</summary>
    Task<ReleasePackageDto> GetAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>按渠道/类型/状态分页列出包。</summary>
    Task<PageResult<ReleasePackageDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? channelKey,
        string? kind,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>删除草稿版本下的包（仅删数据库行，Blob 交由孤儿清理）。已发布/已归档版本拒绝删除。</summary>
    Task<ReleasePackageDto> DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>把摄取失败的包重新置为 <c>pending</c>，交给 Worker 重新处理。</summary>
    Task<ReleasePackageDto> RetryAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>客户端下载定位：包可用且所属版本已发布，否则按不存在/不可用拒绝。</summary>
    Task<ReleasePackageDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>包下载所需信息。</summary>
public sealed record ReleasePackageDownload(
    Guid PackageId,
    Guid ReleaseId,
    string Sha256,
    long SizeBytes,
    string FileName,
    string BlobPath);

/// <summary>
/// 发布包服务。登记只读 Blob 做归属与清单校验，不重复接收大文件；
/// 内容按 SHA-256 与 <see cref="ReleaseFile"/> 共用同一 Blob 路径，靠内容寻址天然去重。
/// </summary>
public sealed partial class ReleasePackageService : IReleasePackageService
{
    private static readonly IReadOnlyDictionary<string, int> FormatPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["exe"] = 0,
            ["msi"] = 1,
            ["msp"] = 2,
            ["zip"] = 3,
            ["7z"] = 4,
            ["bin"] = 5,
        };

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IPackageManifestProbe _probe;
    private readonly IReleaseService _releases;
    private readonly VersionsOptions _options;
    private readonly ILogger<ReleasePackageService> _logger;
    private readonly PlatformMetrics _metrics;

    /// <summary>构造。</summary>
    public ReleasePackageService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IPackageManifestProbe probe,
        IReleaseService releases,
        IOptions<VersionsOptions> options,
        ILogger<ReleasePackageService> logger,
        PlatformMetrics metrics)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _probe = probe;
        _releases = releases;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    [GeneratedRegex(VersionConventions.ChannelKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ChannelKeyRegex();

    /// <inheritdoc />
    public async Task<ReleasePackageDto> RegisterAsync(
        string appKey,
        RegisterReleasePackageRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ReleasePackageDto dto;
        try
        {
            dto = await RegisterCoreAsync(appKey, request, actor, cancellationToken);
        }
        catch (BusinessException)
        {
            IncrementRegister("unknown", "rejected");
            throw;
        }

        var result = dto.Reused ? "reused" : "created";
        IncrementRegister(dto.Kind, result);
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageBytes,
            PlatformMetricNames.VersionsPackageBytesHelp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = dto.Kind },
            dto.SizeBytes);
        return dto;
    }

    private void IncrementRegister(string kind, string result) =>
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageRegister,
            PlatformMetricNames.VersionsPackageRegisterHelp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = kind,
                ["result"] = result,
            });

    private async Task<ReleasePackageDto> RegisterCoreAsync(
        string appKey,
        RegisterReleasePackageRequest request,
        string? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sha256 = PackagePaths.NormalizeSha256(request.Sha256);
        if (!PackagePaths.IsSha256(sha256))
        {
            throw BusinessException.Invalid("sha256 必须是 64 位小写十六进制。");
        }

        // 票据按单文件/包上限的较大值放行；登记只认包上限，避免大文件额度被包复用。
        var maxSizeBytes = _options.MaxPackageSizeBytes;
        if (request.SizeBytes <= 0 || request.SizeBytes > maxSizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageTooLarge,
                $"包大小必须大于 0 且不超过 {maxSizeBytes} 字节。");
        }

        var fileName = NormalizeFileName(request.FileName, sha256);
        var format = ResolveFormat(request.Format, fileName);
        var requestedKind = NormalizeKind(request.Kind);

        var blobPath = VersionBlobPaths.Blob(appKey, sha256);
        var actualLength = await ReadBlobLengthAsync(blobPath, sha256, request.SizeBytes, cancellationToken);

        var probe = await ProbeAsync(
            appKey, sha256, fileName, format, actualLength, request.ManifestSha256, cancellationToken);
        var manifest = probe.Manifest;
        if (manifest?.AppKey is { Length: > 0 } manifestAppKey
            && !string.Equals(manifestAppKey, appKey, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageAppMismatch,
                $"清单属于应用 {manifestAppKey}，不能登记到 {appKey}。");
        }

        if (manifest is not null && manifest.Kind == VersionConventions.PackageKindUpdater)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageKindUnsupported,
                "更新器分发包走独立版本线，不能登记为应用发布包。");
        }

        var kind = ResolveKind(requestedKind, manifest, probe);
        var channelKey = ResolveChannelKey(request.ChannelKey, manifest);
        var version = ResolveVersion(request.Version, manifest);
        var target = ResolveTarget(request.Target, manifest, kind);

        var channel = await _db.Set<ReleaseChannel>()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Key == channelKey,
                cancellationToken);

        if (channel is null)
        {
            // 上传入口的渠道键来自表单或包内清单，要求用户先手建渠道再上传只会输错。
            // 缺就按渠道键自动建一个（名称同键），在渠道列表里标记为自动创建，后续可改名/补受众。
            channel = new ReleaseChannel
            {
                AppKey = appKey,
                Key = channelKey,
                Name = channelKey,
                AutoCreated = true,
            };
            _db.Set<ReleaseChannel>().Add(channel);
        }

        if (!channel.IsEnabled)
        {
            throw BusinessException.Invalid($"渠道 {channelKey} 已停用，不能登记发布包。");
        }

        EnsureFileNameMatches(channelKey, version, kind, fileName);

        var release = await _db.Set<Release>()
            .Include(static x => x.Packages)
            .Include(static x => x.Artifacts)
            .FirstOrDefaultAsync(
                x => x.ChannelId == channel.Id && x.Version == version,
                cancellationToken);

        if (release is not null)
        {
            if (release.Status == VersionConventions.StatusArchived)
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsReleaseImmutable,
                    $"版本 {version} 已归档，不能再登记发布包。");
            }

            EnsureManifestMatchesArtifact(release, manifest, kind, target);
        }

        var existing = release?.Packages.FirstOrDefault(
            x => x.Kind == kind && x.Target == target && x.Format == format);
        if (existing is not null)
        {
            if (string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
            {
                return ToDto(existing, channelKey, release!.Version, reused: true);
            }

            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"版本 {version} 已有 {kind}/{target}/{format} 包（sha256 {existing.Sha256}），与本次内容冲突。");
        }

        var now = _clock.UtcNow;
        var createdRelease = false;
        if (release is null)
        {
            release = new Release
            {
                AppKey = appKey,
                ChannelId = channel.Id,
                Version = version,
                Notes = FirstNonEmpty(request.Notes, manifest?.Description) is { Length: > 0 } notes ? notes : null,
                Status = VersionConventions.StatusDraft,
                CreatedBy = actor,
                Channel = channel,
            };
            _db.Set<Release>().Add(release);
            createdRelease = true;
        }
        else if (release.Status == VersionConventions.StatusDraft
                 && string.IsNullOrWhiteSpace(release.Notes)
                 && !string.IsNullOrWhiteSpace(manifest?.Description))
        {
            // 包内说明只在草稿阶段回填，不覆盖管理员已编辑过的说明。
            release.Notes = manifest.Description;
        }

        var package = new ReleasePackage
        {
            AppKey = appKey,
            Release = release,
            Kind = kind,
            Target = target,
            Format = format,
            FileName = fileName,
            Sha256 = sha256,
            SizeBytes = actualLength,
            ManifestHash = manifest?.HasFiles == true ? manifest.ManifestHash : null,
            ManifestJson = manifest?.ToJson(),
            ManifestSource = manifest?.Source ?? VersionConventions.ManifestSourceManual,
            Status = kind == VersionConventions.PackageKindFull
                ? VersionConventions.PackageStatusReady
                : VersionConventions.PackageStatusPending,
            CompletedAt = kind == VersionConventions.PackageKindFull ? now : null,
            CreatedBy = actor,
            PublishRequested = request.Publish,
            FileCount = manifest?.HasFiles == true
                ? manifest.Files.Count
                : kind == VersionConventions.PackageKindFull ? 1 : null,
        };
        release.Packages.Add(package);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"版本 {version} 的 {kind}/{target}/{format} 包已被其他请求登记。",
                exception);
        }

        if (createdRelease)
        {
            LogDraftReleaseCreated(_logger, appKey, channelKey, release.Version, package.Id);
        }

        if (request.Publish && kind == VersionConventions.PackageKindFull)
        {
            await _releases.PublishAsync(appKey, release.Id, cancellationToken);
            package = await _db.Set<ReleasePackage>()
                .AsNoTracking()
                .SingleAsync(x => x.Id == package.Id, cancellationToken);
        }

        return ToDto(package, channelKey, release.Version, reused: false);
    }

    /// <inheritdoc />
    public async Task<ReleasePackageDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var package = await _db.Set<ReleasePackage>()
                          .AsNoTracking()
                          .Include(static x => x.Release)
                          .ThenInclude(static x => x.Channel)
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw PackageNotFound(id);

        return ToDto(package, package.Release.Channel.Key, package.Release.Version, reused: false);
    }

    /// <inheritdoc />
    public async Task<PageResult<ReleasePackageDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? channelKey,
        string? kind,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = _db.Set<ReleasePackage>()
            .AsNoTracking()
            .Include(static x => x.Release)
            .ThenInclude(static x => x.Channel)
            .Where(x => x.AppKey == appKey);

        if (!string.IsNullOrWhiteSpace(channelKey))
        {
            var key = channelKey.Trim();
            query = query.Where(x => x.Release.Channel.Key == key);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalized = NormalizeKind(kind.Trim())
                ?? throw BusinessException.Invalid("kind 必须是 full / incremental 之一。");
            query = query.Where(x => x.Kind == normalized);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            if (!VersionConventions.IsValidPackageStatus(normalized))
            {
                throw BusinessException.Invalid(
                    "status 必须是 pending / processing / ready / failed 之一。");
            }

            query = query.Where(x => x.Status == normalized);
        }

        return await query
            .OrderByDescending(static x => x.CreatedAt)
            .Select(static x => new ReleasePackageDto(
                x.Id,
                x.ReleaseId,
                x.Release.Channel.Key,
                x.Release.Version,
                x.Kind,
                x.Target,
                x.Format,
                x.FileName,
                x.Sha256,
                x.SizeBytes,
                x.Status,
                x.ManifestSource,
                x.ManifestHash,
                false,
                x.ErrorCode,
                x.ErrorMessage,
                x.CompletedAt,
                x.CreatedAt,
                x.UpdatedAt,
                x.FileCount))
            .ToPageResultAsync(page, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReleasePackageDto> DeleteAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var package = await _db.Set<ReleasePackage>()
                          .Include(static x => x.Release)
                          .ThenInclude(static x => x.Channel)
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw PackageNotFound(id);

        if (package.Release.Status != VersionConventions.StatusDraft)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseImmutable,
                $"版本 {package.Release.Version} 已{StatusText(package.Release.Status)}，请先归档或重新走草稿流程。");
        }

        _db.Set<ReleasePackage>().Remove(package);
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(package, package.Release.Channel.Key, package.Release.Version, reused: false);
    }

    /// <inheritdoc />
    public async Task<ReleasePackageDto> RetryAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var package = await _db.Set<ReleasePackage>()
                          .Include(static x => x.Release)
                          .ThenInclude(static x => x.Channel)
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw PackageNotFound(id);

        if (package.Status != VersionConventions.PackageStatusFailed)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageStatusInvalid,
                $"只有摄取失败的包可以重试（当前 {package.Status}）。");
        }

        if (package.Release.Status == VersionConventions.StatusArchived)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseImmutable,
                $"版本 {package.Release.Version} 已归档，不能重试摄取。");
        }

        package.Status = VersionConventions.PackageStatusPending;
        package.ErrorCode = null;
        package.ErrorMessage = null;
        package.LockedUntil = null;
        package.LockedBy = null;
        package.Attempts = 0;
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(package, package.Release.Channel.Key, package.Release.Version, reused: false);
    }

    /// <inheritdoc />
    public async Task<ReleasePackageDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var package = await _db.Set<ReleasePackage>()
                          .AsNoTracking()
                          .Include(static x => x.Release)
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw PackageNotFound(id);

        if (package.Release.Status != VersionConventions.StatusPublished)
        {
            throw PackageNotFound(id);
        }

        if (package.Status != VersionConventions.PackageStatusReady)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageNotReady,
                $"包 {package.FileName} 尚未就绪（{package.Status}）。");
        }

        var blobPath = VersionBlobPaths.Blob(appKey, package.Sha256);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw PackageNotFound(id);
        }

        return new ReleasePackageDownload(
            package.Id,
            package.ReleaseId,
            package.Sha256,
            package.SizeBytes,
            package.FileName,
            blobPath);
    }

    /// <summary>按 §2.5 选安装包：同 RID 优先，其次 any；再按格式优先级与登记时间。</summary>
    public static ReleasePackage? SelectInstaller(IEnumerable<ReleasePackage> packages, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(runtimeIdentifier);

        return packages
            .Where(static x => x.Kind == VersionConventions.PackageKindFull
                               && x.Status == VersionConventions.PackageStatusReady)
            .Where(x => x.Target == runtimeIdentifier || x.Target == VersionConventions.PackageTargetAny)
            .OrderByDescending(x => string.Equals(x.Target, runtimeIdentifier, StringComparison.Ordinal))
            .ThenBy(static x => FormatPriority.GetValueOrDefault(x.Format, int.MaxValue))
            .ThenByDescending(static x => x.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>增量包只认同 RID 的 ready 包（不接受 any）。</summary>
    public static ReleasePackage? SelectIncremental(IEnumerable<ReleasePackage> packages, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(runtimeIdentifier);

        return packages
            .Where(static x => x.Kind == VersionConventions.PackageKindIncremental
                               && x.Status == VersionConventions.PackageStatusReady)
            .Where(x => x.Target == runtimeIdentifier)
            .OrderByDescending(static x => x.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>包 DTO 映射（管理台与 check 共用）。</summary>
    public static ReleasePackageDto ToDto(
        ReleasePackage package,
        string channelKey,
        string version,
        bool reused) =>
        new(
            package.Id,
            package.ReleaseId,
            channelKey,
            version,
            package.Kind,
            package.Target,
            package.Format,
            package.FileName,
            package.Sha256,
            package.SizeBytes,
            package.Status,
            package.ManifestSource,
            package.ManifestHash,
            reused,
            package.ErrorCode,
            package.ErrorMessage,
            package.CompletedAt,
            package.CreatedAt,
            package.UpdatedAt,
            package.FileCount);

    private static BusinessException PackageNotFound(Guid id) =>
        BusinessException.NotFound(ApiErrorCode.VersionsPackageNotFound, $"发布包 {id} 不存在。");

    private static string StatusText(string status) => status switch
    {
        VersionConventions.StatusPublished => "发布",
        VersionConventions.StatusArchived => "归档",
        _ => status,
    };

    private async Task<long> ReadBlobLengthAsync(
        string blobPath,
        string sha256,
        long declaredSize,
        CancellationToken cancellationToken)
    {
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                $"内容 {sha256} 尚未上传，请先走上传票据直传。");
        }

        var actual = await _blobs.GetLengthAsync(blobPath, cancellationToken);
        if (actual != declaredSize)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                $"包内容长度与声明不符（声明 {declaredSize}，实际 {actual}）。");
        }

        return actual;
    }

    private async Task<PackageProbeResult> ProbeAsync(
        string appKey,
        string sha256,
        string fileName,
        string format,
        long sizeBytes,
        string? manifestSha256,
        CancellationToken cancellationToken)
    {
        string? sidecarPath = null;
        if (!string.IsNullOrWhiteSpace(manifestSha256))
        {
            var sidecarSha = PackagePaths.NormalizeSha256(manifestSha256);
            if (!PackagePaths.IsSha256(sidecarSha))
            {
                throw BusinessException.Invalid("manifestSha256 必须是 64 位小写十六进制。");
            }

            sidecarPath = VersionBlobPaths.Blob(appKey, sidecarSha);
            if (!await _blobs.ExistsAsync(sidecarPath, cancellationToken))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsPackageHashMismatch,
                    $"sidecar 清单 {sidecarSha} 尚未上传。");
            }
        }

        var packageStream = await _blobs.OpenReadAsync(
            VersionBlobPaths.Blob(appKey, sha256), cancellationToken);
        Stream? sidecarStream = null;
        try
        {
            if (sidecarPath is not null)
            {
                sidecarStream = await _blobs.OpenReadAsync(sidecarPath, cancellationToken);
            }

            var result = _probe.Probe(new PackageProbeRequest(
                appKey,
                fileName,
                format,
                sizeBytes,
                () => packageStream,
                sidecarStream is null ? null : () => sidecarStream));
            _metrics.Increment(
                PlatformMetricNames.VersionsPackageProbe,
                PlatformMetricNames.VersionsPackageProbeHelp,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["format"] = format,
                    ["result"] = result.Success ? "success" : "missing",
                });
            return result;
        }
        finally
        {
            await packageStream.DisposeAsync();
            if (sidecarStream is not null)
            {
                await sidecarStream.DisposeAsync();
            }
        }
    }

    private static string NormalizeFileName(string? raw, string sha256)
    {
        var fileName = raw?.Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            return sha256;
        }

        if (fileName.Length > 256
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains('\0')
            || fileName.Any(char.IsControl))
        {
            throw BusinessException.Invalid($"包文件名非法：{raw}。");
        }

        return fileName;
    }

    private static string ResolveFormat(string? raw, string fileName)
    {
        var format = raw?.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(format))
        {
            format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        }

        if (!VersionConventions.IsSupportedPackageFormat(format))
        {
            throw BusinessException.Invalid(
                $"包格式 {raw} 不支持，应为 exe / msi / msp / zip / 7z / bin 之一。");
        }

        return format;
    }

    private static string? NormalizeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var kind = raw.Trim().ToLowerInvariant();
        if (!VersionConventions.IsSupportedPackageKind(kind))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageKindUnsupported,
                $"包类型 {raw} 不支持，应为 full / incremental 之一。");
        }

        return kind;
    }

    private static string ResolveKind(
        string? requestedKind,
        PackageManifest? manifest,
        PackageProbeResult probe)
    {
        if (manifest is not null)
        {
            if (requestedKind is not null
                && !string.Equals(requestedKind, manifest.Kind, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsPackageKindUnsupported,
                    $"清单 kind={manifest.Kind} 与请求 kind={requestedKind} 不一致。");
            }

            return manifest.Kind;
        }

        if (requestedKind == VersionConventions.PackageKindIncremental)
        {
            // 坏包/越界/清单非法要保留原始错误码，不能被「缺清单」掩盖。
            if (probe.ErrorCode is { } code && code != ApiErrorCode.VersionsPackageManifestMissing)
            {
                throw new BusinessException(code, probe.ErrorMessage ?? "增量包清单无效。");
            }

            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestMissing,
                $"增量包必须带清单：{probe.ErrorMessage}");
        }

        if (requestedKind != VersionConventions.PackageKindFull)
        {
            // full 允许人工登记，但必须先显式声明 kind，避免把坏包悄悄当成安装包。
            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestMissing,
                $"{probe.ErrorMessage} 上传入口只按包内清单登记；请用 build-installer/build-platform-package "
                + "等构建脚本产出带清单的安装包，或通过 API 显式指定 kind=full 并填写 channelKey/version 人工登记。");
        }

        return VersionConventions.PackageKindFull;
    }

    private static string ResolveChannelKey(string? requested, PackageManifest? manifest)
    {
        var channelKey = FirstNonEmpty(requested, manifest?.ChannelKey);
        if (string.IsNullOrEmpty(channelKey) || !ChannelKeyRegex().IsMatch(channelKey))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"无法确定合法渠道键（请求 {requested}，清单 {manifest?.ChannelKey}）。");
        }

        if (!string.IsNullOrWhiteSpace(requested)
            && !string.IsNullOrWhiteSpace(manifest?.ChannelKey)
            && !string.Equals(requested.Trim(), manifest.ChannelKey, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单渠道 {manifest.ChannelKey} 与请求渠道 {requested} 不一致。");
        }

        return channelKey;
    }

    private static string ResolveVersion(string? requested, PackageManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(requested)
            && !string.IsNullOrWhiteSpace(manifest?.Version)
            && !VersionsEqual(requested, manifest.Version))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单版本 {manifest.Version} 与请求版本 {requested} 不一致。");
        }

        var version = FirstNonEmpty(requested, manifest?.Version);
        if (!PackageManifest.IsValidVersion(version))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"无法确定合法版本号（请求 {requested}，清单 {manifest?.Version}）。");
        }

        return version;
    }

    private static string ResolveTarget(string? requested, PackageManifest? manifest, string kind)
    {
        var requestedTarget = requested?.Trim().ToLowerInvariant();
        var manifestTarget = manifest?.RuntimeIdentifier?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(requestedTarget)
            && !string.IsNullOrEmpty(manifestTarget)
            && !string.Equals(requestedTarget, manifestTarget, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageChannelVersionMismatch,
                $"清单运行时 {manifestTarget} 与请求 target {requestedTarget} 不一致。");
        }

        var target = FirstNonEmpty(requestedTarget, manifestTarget);
        if (kind == VersionConventions.PackageKindIncremental)
        {
            if (!VersionConventions.IsSupportedRuntime(target))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                    $"增量包必须指定具体运行时（当前 {target}）。");
            }

            return target;
        }

        if (string.IsNullOrEmpty(target))
        {
            return VersionConventions.PackageTargetAny;
        }

        if (!VersionConventions.IsSupportedRuntime(target)
            && !string.Equals(target, VersionConventions.PackageTargetAny, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                $"不支持的 target：{requested ?? manifest?.RuntimeIdentifier}。");
        }

        return target;
    }

    private void EnsureFileNameMatches(string channelKey, string version, string kind, string fileName)
    {
        var patterns = _options.PackageFileNamePatterns
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0)
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            var regex = FileNamePatternRegex(pattern);
            if (regex.IsMatch(fileName))
            {
                return;
            }
        }

        if (_options.RequirePackageFileNameMatch)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageFileNameMismatch,
                $"包文件名 {fileName} 不匹配约定模板（{_options.PackageFileNamePatterns}）。");
        }

        LogFileNameMismatch(_logger, fileName, channelKey, version, kind);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "发布包登记时自动创建草稿版本 {AppKey}/{ChannelKey}/{Version}（{PackageId}）。")]
    private static partial void LogDraftReleaseCreated(
        ILogger logger, string appKey, string channelKey, string version, Guid packageId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "包文件名 {FileName} 不匹配约定模板（channel={ChannelKey}, version={Version}, kind={Kind}）。")]
    private static partial void LogFileNameMismatch(
        ILogger logger, string fileName, string channelKey, string version, string kind);

    private static void EnsureManifestMatchesArtifact(
        Release release,
        PackageManifest? manifest,
        string kind,
        string target)
    {
        if (manifest is not { HasFiles: true })
        {
            return;
        }

        var artifact = release.Artifacts.FirstOrDefault(
            x => string.Equals(x.RuntimeIdentifier, target, StringComparison.Ordinal));
        if (artifact is not null
            && !string.Equals(artifact.ManifestHash, manifest.ManifestHash, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageManifestConflict,
                $"包清单与版本 {release.Version}/{target} 的产物清单不一致"
                + $"（版本 {artifact.ManifestHash}，包 {manifest.ManifestHash}，kind={kind}）。");
        }
    }

    private static bool VersionsEqual(string left, string right)
    {
        if (string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return VersionComparer.TryCompare(left, right, out var comparison) && comparison == 0;
    }

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? string.Empty;

    private static Regex FileNamePatternRegex(string pattern) =>
        new(
            "^" + BuildPattern(pattern) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BuildPattern(string pattern)
    {
        var builder = new System.Text.StringBuilder(pattern.Length * 2);
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '{')
            {
                var end = pattern.IndexOf('}', index);
                if (end > index)
                {
                    var token = pattern[(index + 1)..end];
                    var fragment = token switch
                    {
                        "channel" => "[a-z0-9-]+",
                        "version" => "[0-9A-Za-z.+-]+",
                        "kind" => "[a-z]+",
                        "ext" => @"\.[a-z0-9]+",
                        _ => null,
                    };
                    if (fragment is not null)
                    {
                        builder.Append(fragment);
                        index = end;
                        continue;
                    }
                }
            }

            builder.Append(Regex.Escape(pattern[index].ToString()));
        }

        return builder.ToString();
    }
}
