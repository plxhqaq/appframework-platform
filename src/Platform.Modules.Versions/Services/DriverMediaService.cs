using System.Text.Json;
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

/// <summary>驱动媒体包登记与查询（平台侧唯一入口；安装器只消费解压后的 <c>media/</c> 目录）。</summary>
public interface IDriverMediaService
{
    /// <summary>登记媒体包：内容先经上传票据直传，清单与 zip 逐文件复核后落库。</summary>
    Task<DriverMediaDto> RegisterAsync(
        string appKey,
        RegisterDriverMediaRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>分页列出媒体包。</summary>
    Task<PageResult<DriverMediaDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? mediaKey,
        bool? activeOnly,
        CancellationToken cancellationToken = default);

    /// <summary>按主键取媒体包详情。</summary>
    Task<DriverMediaDto> GetAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>把媒体键的启用版本切到指定行（同键其余全部置非启用）。</summary>
    Task<DriverMediaDto> ActivateAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>删除登记行；blob 交给孤儿扫描回收。</summary>
    Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>下载定位：登记行与 blob 都必须存在。</summary>
    Task<DriverMediaDownload> GetDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 驱动媒体实现。同一 (mediaKey, version) 只允许一份内容：同 sha 幂等复用，异 sha 冲突拒绝；
/// 每个 mediaKey 至多一条启用行（首次登记自动启用）。
/// </summary>
public sealed partial class DriverMediaService : IDriverMediaService
{
    private static readonly Action<ILogger, string, string, string, Guid, long, int, Exception?> LogRegistered =
        LoggerMessage.Define<string, string, string, Guid, long, int>(
            LogLevel.Information,
            new EventId(1, "DriverMediaRegistered"),
            "登记驱动媒体 {AppKey}/{MediaKey}/{Version}（{Id}，{Bytes} 字节，{Files} 个文件）");

    private static readonly Action<ILogger, string, string, Guid, Exception?> LogDeleted =
        LoggerMessage.Define<string, string, Guid>(
            LogLevel.Information,
            new EventId(2, "DriverMediaDeleted"),
            "删除驱动媒体登记 {AppKey}/{MediaKey}/{Id}");

    private static readonly Regex MediaKeyRegex = CompileMediaKey();
    private static readonly Regex VersionRegex = CompileVersion();

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly VersionsOptions _options;
    private readonly PlatformMetrics _metrics;
    private readonly ILogger<DriverMediaService> _logger;

    /// <summary>构造。</summary>
    public DriverMediaService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<VersionsOptions> options,
        PlatformMetrics metrics,
        ILogger<DriverMediaService> logger)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DriverMediaDto> RegisterAsync(
        string appKey,
        RegisterDriverMediaRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sha256 = PackagePaths.NormalizeSha256(request.Sha256);
        if (!PackagePaths.IsSha256(sha256))
        {
            throw BusinessException.Invalid("sha256 必须是 64 位小写十六进制。");
        }

        if (request.SizeBytes <= 0 || request.SizeBytes > VersionConventions.MaxMediaSizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageTooLarge,
                $"媒体包大小必须大于 0 且不超过 {VersionConventions.MaxMediaSizeBytes} 字节。");
        }

        var fileName = NormalizeZipFileName(request.FileName, sha256);
        var blobPath = VersionBlobPaths.Blob(appKey, sha256);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                "媒体内容尚未上传，请先走上传票据直传。");
        }

        var actualLength = await _blobs.GetLengthAsync(blobPath, cancellationToken);
        if (actualLength != request.SizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageHashMismatch,
                $"媒体内容长度 {actualLength} 与声明 {request.SizeBytes} 不一致。");
        }

        var manifest = await ProbeManifestAsync(blobPath, cancellationToken);

        var mediaKey = ResolveMediaKey(request.MediaKey, manifest.ComponentId);
        var version = ResolveVersion(request.Version, manifest.Version);
        var name = ResolveName(request.Name, manifest.Label);

        var existing = await _db.Set<DriverMedia>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.MediaKey == mediaKey && x.Version == version,
                cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
            {
                return ToDto(existing, reused: true);
            }

            throw new BusinessException(
                ApiErrorCode.VersionsMediaKeyExists,
                $"媒体 {mediaKey}/{version} 已登记另一份内容（sha256 {existing.Sha256}）。");
        }

        var anyActiveForMediaKey = await _db.Set<DriverMedia>()
            .AnyAsync(x => x.AppKey == appKey && x.MediaKey == mediaKey && x.IsActive, cancellationToken);

        var entity = new DriverMedia
        {
            AppKey = appKey,
            MediaKey = mediaKey,
            Name = name,
            Version = version,
            IsActive = !anyActiveForMediaKey,
            FileName = fileName,
            Sha256 = sha256,
            SizeBytes = actualLength,
            FileCount = manifest.Files.Count,
            ManifestHash = manifest.ManifestHash,
            ManifestJson = manifest.ToJson(),
            InstallOrderJson = manifest.InstallOrder.Count > 0
                ? JsonSerializer.Serialize(manifest.InstallOrder)
                : null,
            CreatedBy = actor,
        };
        _db.Set<DriverMedia>().Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            var raced = await _db.Set<DriverMedia>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.AppKey == appKey && x.MediaKey == mediaKey && x.Version == version,
                    cancellationToken);
            if (raced is not null && string.Equals(raced.Sha256, sha256, StringComparison.Ordinal))
            {
                return ToDto(raced, reused: true);
            }

            throw new BusinessException(
                ApiErrorCode.VersionsMediaKeyExists,
                $"媒体 {mediaKey}/{version} 已被其他请求登记。",
                exception);
        }

        Increment("accepted");
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageBytes,
            PlatformMetricNames.VersionsPackageBytesHelp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = "media" },
            entity.SizeBytes);
        LogRegistered(
            _logger,
            appKey,
            mediaKey,
            version.Length == 0 ? "-" : version,
            entity.Id,
            entity.SizeBytes,
            entity.FileCount,
            null);
        return ToDto(entity, reused: false);
    }

    /// <inheritdoc />
    public async Task<PageResult<DriverMediaDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? mediaKey,
        bool? activeOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = _db.Set<DriverMedia>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey);
        if (!string.IsNullOrWhiteSpace(mediaKey))
        {
            var value = mediaKey.Trim().ToLowerInvariant();
            query = query.Where(x => x.MediaKey == value);
        }

        if (activeOnly == true)
        {
            query = query.Where(x => x.IsActive);
        }

        var result = await query
            .OrderBy(static x => x.MediaKey)
            .ThenByDescending(static x => x.CreatedAt)
            .ToPageResultAsync(page, cancellationToken);
        return PageResult<DriverMediaDto>.Create(
            [.. result.Items.Select(static x => ToDto(x, reused: false))],
            result.Total,
            page);
    }

    /// <inheritdoc />
    public async Task<DriverMediaDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var media = await FindTrackedAsync(appKey, id, cancellationToken);
        return ToDto(media, reused: false);
    }

    /// <inheritdoc />
    public async Task<DriverMediaDto> ActivateAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var media = await FindTrackedAsync(appKey, id, cancellationToken);
        if (!media.IsActive)
        {
            var siblings = await _db.Set<DriverMedia>()
                .Where(x => x.AppKey == appKey
                            && x.MediaKey == media.MediaKey
                            && x.Id != media.Id
                            && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var sibling in siblings)
            {
                sibling.IsActive = false;
            }

            media.IsActive = true;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(media, reused: false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default)
    {
        var media = await FindTrackedAsync(appKey, id, cancellationToken);
        _db.Set<DriverMedia>().Remove(media);
        await _db.SaveChangesAsync(cancellationToken);
        LogDeleted(_logger, appKey, media.MediaKey, id, null);
    }

    /// <inheritdoc />
    public async Task<DriverMediaDownload> GetDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var media = await _db.Set<DriverMedia>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.VersionsMediaNotFound,
                $"媒体包 {id} 不存在。");

        var blobPath = VersionBlobPaths.Blob(appKey, media.Sha256);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw BusinessException.NotFound(
                ApiErrorCode.VersionsMediaNotFound,
                $"媒体包 {id} 的内容已丢失（blob {media.Sha256} 不在存储中）。");
        }

        return new DriverMediaDownload(media.Id, media.Sha256, media.SizeBytes, media.FileName, blobPath);
    }

    private async Task<MediaManifest> ProbeManifestAsync(string blobPath, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.OpenReadAsync(blobPath, cancellationToken);
        var probe = ZipMediaManifestProbe.TryRead(stream, _options);
        if (!probe.Success || probe.Manifest is null)
        {
            Increment("rejected");
            throw new BusinessException(
                ApiErrorCode.VersionsMediaManifestInvalid,
                probe.ErrorMessage ?? "媒体清单校验未通过。");
        }

        return probe.Manifest;
    }

    private void Increment(string result) =>
        _metrics.Increment(
            PlatformMetricNames.VersionsPackageRegister,
            PlatformMetricNames.VersionsPackageRegisterHelp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "media",
                ["result"] = result,
            });

    private async Task<DriverMedia> FindTrackedAsync(string appKey, Guid id, CancellationToken cancellationToken) =>
        await _db.Set<DriverMedia>()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Id == id, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.VersionsMediaNotFound,
                $"媒体包 {id} 不存在。");

    private static string NormalizeZipFileName(string? raw, string sha256)
    {
        var fileName = raw?.Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            return $"{sha256}.zip";
        }

        if (fileName.Length > 256
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains('\0')
            || fileName.Any(char.IsControl))
        {
            throw BusinessException.Invalid($"媒体包文件名非法：{raw}。");
        }

        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw BusinessException.Invalid("媒体包必须是 zip 文件。");
        }

        return fileName;
    }

    private static string ResolveMediaKey(string? requested, string componentId)
    {
        var mediaKey = requested?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mediaKey))
        {
            mediaKey = componentId.ToLowerInvariant();
            if (!MediaKeyRegex.IsMatch(mediaKey))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsMediaManifestInvalid,
                    $"清单 componentId「{componentId}」不满足媒体键口径（{VersionConventions.MediaKeyPattern}）。");
            }

            return mediaKey;
        }

        if (!MediaKeyRegex.IsMatch(mediaKey))
        {
            throw BusinessException.Invalid($"mediaKey 非法（应为 {VersionConventions.MediaKeyPattern}）：{requested}。");
        }

        if (!string.Equals(mediaKey, componentId, StringComparison.OrdinalIgnoreCase))
        {
            throw BusinessException.Invalid(
                $"mediaKey「{mediaKey}」与清单 componentId「{componentId}」不一致。");
        }

        return mediaKey;
    }

    private static string ResolveVersion(string? requested, string manifestVersion)
    {
        var version = (requested?.Trim() ?? string.Empty).Length > 0 ? requested!.Trim() : manifestVersion;
        if (version.Length == 0)
        {
            return VersionConventions.MediaVersionUnspecified;
        }

        if (!VersionRegex.IsMatch(version))
        {
            throw BusinessException.Invalid($"version 不符合三段/四段版本口径：{version}。");
        }

        return version;
    }

    private static string ResolveName(string? requested, string label)
    {
        var name = (requested?.Trim() ?? string.Empty).Length > 0 ? requested!.Trim() : label;
        if (name.Length > 256)
        {
            throw BusinessException.Invalid("name 长度不能超过 256。");
        }

        return name;
    }

    private static DriverMediaDto ToDto(DriverMedia media, bool reused) => new(
        media.Id,
        media.MediaKey,
        media.Name,
        media.Version,
        media.IsActive,
        media.FileName,
        media.Sha256,
        media.SizeBytes,
        media.FileCount,
        media.ManifestHash,
        ParseInstallOrder(media.InstallOrderJson),
        media.CreatedBy,
        media.CreatedAt,
        media.UpdatedAt,
        reused);

    private static List<string> ParseInstallOrder(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    [GeneratedRegex(VersionConventions.MediaKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex CompileMediaKey();

    [GeneratedRegex(VersionConventions.VersionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex CompileVersion();
}
