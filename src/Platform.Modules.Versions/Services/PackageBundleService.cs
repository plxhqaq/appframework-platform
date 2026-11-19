using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Packages;

namespace Platform.Modules.Versions.Services;

/// <summary>
/// 完整全量包组合下载：全量包 exe + 所选驱动媒体 zip 内容 + <c>media/media-order.json</c>
/// 合成为一个确定性 zip，按组合哈希缓存（纯派生内容，删了可原样重建）。
/// </summary>
public interface IPackageBundleService
{
    /// <summary>
    /// 签发前校验并给出组合信息：包必须为就绪全量包、媒体齐属同应用且 blob 在架、
    /// 声明覆盖检查（<paramref name="allowMissing"/> 强过时把缺项带回供审计）。
    /// </summary>
    Task<PackageBundlePrepared> PrepareAsync(
        string appKey,
        Guid packageId,
        IReadOnlyList<Guid> mediaIds,
        bool allowMissing = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载兑现：按包 id + 媒体 id 重算组合，缓存缺失时流式合成，返回实际下发文件定位。
    /// 声明覆盖只在签发时把关，这里不再判（媒体被删则按缺失 404）。
    /// </summary>
    Task<PackageBundleDownload> EnsureAsync(
        string appKey,
        Guid packageId,
        IReadOnlyList<Guid> mediaIds,
        CancellationToken cancellationToken = default);
}

/// <summary>组合签发信息：组合哈希、下发文件名、预估字节数与未被声明覆盖的媒体组件。</summary>
public sealed record PackageBundlePrepared(
    string CompositionHash,
    string FileName,
    long EstimatedSizeBytes,
    IReadOnlyList<string> MissingComponents);

/// <summary>组合下载定位：缓存 blob、文件名、实际字节数与组合哈希（作内容摘要下发）。</summary>
public sealed record PackageBundleDownload(
    string BlobPath,
    string FileName,
    long SizeBytes,
    string CompositionHash);

/// <summary>
/// 实现要点：
/// <list type="bullet">
/// <item>组合哈希 = sha256("<c>{包sha}:{排序去重后的媒体sha用冒号拼接}</c>")，同组合必同键；</item>
/// <item>条目确定序：包根条目 → 各媒体（按 媒体键/版本/sha 排序）内按路径排序的文件 → 顺序清单；</item>
/// <item>同路径同 sha 去重只写一份；同路径异 sha 视为冲突 409；</item>
/// <item>合成走 <see cref="IZipComposer"/>，命中缓存直接复用。</item>
/// </list>
/// </summary>
public sealed class PackageBundleService : IPackageBundleService
{
    private static readonly JsonSerializerOptions OrderJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IZipComposer _composer;

    /// <summary>构造。</summary>
    public PackageBundleService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IZipComposer composer)
    {
        _db = db;
        _blobs = blobs;
        _composer = composer;
    }

    /// <inheritdoc />
    public async Task<PackageBundlePrepared> PrepareAsync(
        string appKey,
        Guid packageId,
        IReadOnlyList<Guid> mediaIds,
        bool allowMissing = false,
        CancellationToken cancellationToken = default)
    {
        var plan = await BuildPlanAsync(appKey, packageId, mediaIds, gateCoverage: true, allowMissing, cancellationToken);
        return new PackageBundlePrepared(
            plan.CompositionHash,
            plan.FileName,
            plan.PackageSizeBytes + plan.UniqueMediaBytes,
            plan.MissingComponents);
    }

    /// <inheritdoc />
    public async Task<PackageBundleDownload> EnsureAsync(
        string appKey,
        Guid packageId,
        IReadOnlyList<Guid> mediaIds,
        CancellationToken cancellationToken = default)
    {
        var plan = await BuildPlanAsync(
            appKey, packageId, mediaIds, gateCoverage: false, allowMissing: true, cancellationToken);
        var result = await _composer.ComposeAsync(
            plan.BlobPath, BuildItems(plan), cancellationToken);
        return new PackageBundleDownload(result.BlobPath, plan.FileName, result.SizeBytes, plan.CompositionHash);
    }

    private async Task<BundlePlan> BuildPlanAsync(
        string appKey,
        Guid packageId,
        IReadOnlyList<Guid> mediaIds,
        bool gateCoverage,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        var ids = (mediaIds ?? []).Distinct().ToArray();
        if (ids.Contains(Guid.Empty))
        {
            throw BusinessException.Invalid("媒体 id 非法。");
        }

        if (ids.Length > VersionConventions.MaxBundleMediaPackages)
        {
            throw BusinessException.Invalid(
                $"一次组合最多选择 {VersionConventions.MaxBundleMediaPackages} 份媒体。");
        }

        var package = await _db.Set<ReleasePackage>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == packageId && x.AppKey == appKey, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.VersionsPackageNotFound, $"包 {packageId} 不存在。");

        if (package.Kind != VersionConventions.PackageKindFull)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageKindUnsupported, "仅全量安装包支持组合下载。");
        }

        if (package.Status != VersionConventions.PackageStatusReady)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsPackageNotReady,
                $"包 {package.FileName} 尚未就绪（{package.Status}）。");
        }

        var packageBlobPath = VersionBlobPaths.Blob(appKey, package.Sha256);
        if (!await _blobs.ExistsAsync(packageBlobPath, cancellationToken))
        {
            throw BusinessException.NotFound(
                ApiErrorCode.VersionsPackageNotFound,
                $"包 {packageId} 的内容已丢失（blob {package.Sha256} 不在存储中）。");
        }

        var rows = ids.Length == 0
            ? []
            : await _db.Set<DriverMedia>()
                .AsNoTracking()
                .Where(x => x.AppKey == appKey && ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
        var found = rows.Select(static x => x.Id).ToHashSet();
        var missingRow = ids.FirstOrDefault(id => !found.Contains(id));
        if (missingRow != Guid.Empty)
        {
            throw BusinessException.NotFound(
                ApiErrorCode.VersionsMediaNotFound, $"媒体包 {missingRow} 不存在。");
        }

        var medias = new List<BundleMediaPlan>(rows.Count);
        var uniqueMediaBytes = 0L;
        foreach (var row in rows.OrderBy(static x => x.MediaKey, StringComparer.Ordinal)
                     .ThenBy(static x => x.Version, StringComparer.Ordinal)
                     .ThenBy(static x => x.Sha256, StringComparer.Ordinal))
        {
            if (!MediaManifest.TryParseJson(row.ManifestJson ?? string.Empty, out var manifest, out var error)
                || manifest is null)
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsMediaManifestInvalid,
                    $"媒体包 {row.MediaKey} 的清单不可用：{error ?? "清单为空"}");
            }

            var mediaBlobPath = VersionBlobPaths.Blob(appKey, row.Sha256);
            if (!await _blobs.ExistsAsync(mediaBlobPath, cancellationToken))
            {
                throw BusinessException.NotFound(
                    ApiErrorCode.VersionsMediaNotFound,
                    $"媒体包 {row.MediaKey} 的内容已丢失（blob {row.Sha256} 不在存储中）。");
            }

            medias.Add(new BundleMediaPlan(
                row.Id,
                row.MediaKey,
                row.Sha256,
                mediaBlobPath,
                row.ManifestHash ?? manifest.ManifestHash,
                manifest.Files.OrderBy(static f => f.Path, StringComparer.Ordinal).ToArray(),
                [.. manifest.InstallOrder],
                [.. manifest.PackageOrder]));
        }

        // 路径归并：同路径同内容（文件 sha）去重只写一份；同路径异内容视为冲突。
        // 注意比较的是文件级 sha：两份不同媒体 zip 完全可以携带同一份文件。
        var claimed = new Dictionary<string, (string Sha, string MediaKey)>(StringComparer.Ordinal);
        foreach (var media in medias)
        {
            foreach (var file in media.Files)
            {
                if (string.Equals(file.Path, VersionConventions.MediaOrderEntryPath, StringComparison.Ordinal))
                {
                    throw new BusinessException(
                        ApiErrorCode.VersionsBundlePathConflict,
                        $"媒体 {media.MediaKey} 的条目占用了保留路径 {file.Path}。");
                }

                if (!claimed.TryGetValue(file.Path, out var previous))
                {
                    claimed[file.Path] = (file.Sha256, media.MediaKey);
                    uniqueMediaBytes += file.SizeBytes;
                }
                else if (!string.Equals(previous.Sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BusinessException(
                        ApiErrorCode.VersionsBundlePathConflict,
                        $"路径 {file.Path} 在媒体 {previous.MediaKey} 与 {media.MediaKey} 中内容不一致。");
                }
            }
        }

        // 声明覆盖：包清单缺 mediaRequirements 时按"任选"处理（无门禁）。
        string[] missingComponents = [];
        if (gateCoverage)
        {
            var requirements = PackageManifest.TryParseJson(
                package.ManifestJson, package.ManifestSource, out var packageManifest, out _)
                && packageManifest is not null
                    ? packageManifest.MediaRequirements
                    : [];
            if (requirements.Count > 0)
            {
                var selectedKeys = medias
                    .Select(static m => m.MediaKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                missingComponents = requirements
                    .Select(static r => r.ComponentId)
                    .Where(componentId => !selectedKeys.Contains(componentId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static x => x, StringComparer.Ordinal)
                    .ToArray();
                if (missingComponents.Length > 0 && !allowMissing)
                {
                    throw new BusinessException(
                        ApiErrorCode.VersionsBundleMediaMissing,
                        $"全量包声明的媒体组件未选中：{string.Join("、", missingComponents)}。" +
                        "如确需缺介质组合，请勾选允许缺项。");
                }
            }
        }

        var compositionHash = ComputeCompositionHash(package.Sha256, medias);
        return new BundlePlan(
            ZipComposer.SafeEntryName(package.FileName),
            $"{ZipComposer.SafeEntryName(package.FileName)}.zip",
            packageBlobPath,
            package.SizeBytes,
            uniqueMediaBytes,
            VersionBlobPaths.Bundle(appKey, compositionHash),
            compositionHash,
            medias,
            missingComponents);
    }

    private static string ComputeCompositionHash(string packageSha256, IReadOnlyList<BundleMediaPlan> medias)
    {
        var mediaShas = medias
            .Select(static m => m.Sha256)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal);
        var input = $"{packageSha256}:{string.Join(':', mediaShas)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static List<ZipCompositionItem> BuildItems(BundlePlan plan)
    {
        var items = new List<ZipCompositionItem>(capacity: 1 + plan.Medias.Sum(static m => m.Files.Count) + 1)
        {
            new(plan.RootEntryName, ZipItemKind.WholeBlob, SourceBlobPath: plan.PackageBlobPath),
        };

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var media in plan.Medias)
        {
            foreach (var file in media.Files)
            {
                // 同路径异内容已在建计划时拒绝；同内容（同 sha）只写第一份。
                if (emitted.Add(file.Path))
                {
                    items.Add(new ZipCompositionItem(
                        file.Path, ZipItemKind.ExtractEntry, media.BlobPath, file.Path));
                }
            }
        }

        if (plan.Medias.Count > 0)
        {
            var orderDocument = new MediaOrderDocument(
                VersionConventions.MediaOrderSchemaV1,
                plan.Medias
                    .Select(static m => new MediaOrderEntry(
                        m.MediaKey,
                        m.ManifestHash,
                        m.InstallOrder,
                        m.PackageOrder.Count > 0 ? m.PackageOrder : null))
                    .ToArray());
            items.Add(new ZipCompositionItem(
                VersionConventions.MediaOrderEntryPath,
                ZipItemKind.InlineText,
                InlineText: JsonSerializer.Serialize(orderDocument, OrderJsonOptions)));
        }

        return items;
    }

    private sealed record BundleMediaPlan(
        Guid Id,
        string MediaKey,
        string Sha256,
        string BlobPath,
        string ManifestHash,
        IReadOnlyList<ReleaseFileDto> Files,
        IReadOnlyList<string> InstallOrder,
        IReadOnlyList<string> PackageOrder);

    private sealed record BundlePlan(
        string RootEntryName,
        string FileName,
        string PackageBlobPath,
        long PackageSizeBytes,
        long UniqueMediaBytes,
        string BlobPath,
        string CompositionHash,
        IReadOnlyList<BundleMediaPlan> Medias,
        IReadOnlyList<string> MissingComponents);

    private sealed record MediaOrderDocument(string Schema, IReadOnlyList<MediaOrderEntry> Entries);

    private sealed record MediaOrderEntry(
        string MediaKey,
        string ManifestHash,
        IReadOnlyList<string> InstallOrder,
        IReadOnlyList<string>? PackageOrder);
}
