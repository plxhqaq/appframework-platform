using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Versions.Domain;

namespace Platform.Modules.Versions.Services;

/// <summary>下载前的内容定位：确认该哈希属于当前应用的已发布版本，并返回 Blob 路径。</summary>
public interface IReleaseContentService
{
    /// <summary>解析内容信息；不属于本应用或未发布时按 <see cref="ApiErrorCode.VersionsArtifactNotFound"/> 拒绝。</summary>
    Task<ReleaseContentInfo> ResolveAsync(
        string appKey,
        string sha256,
        CancellationToken cancellationToken = default);
}

/// <summary>内容定位结果。</summary>
public sealed record ReleaseContentInfo(string Sha256, long SizeBytes, string BlobPath);

/// <summary>内容定位实现。</summary>
public sealed class ReleaseContentService : IReleaseContentService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;

    /// <summary>构造。</summary>
    public ReleaseContentService(PlatformDbContext db, IBlobStorage blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    /// <inheritdoc />
    public async Task<ReleaseContentInfo> ResolveAsync(
        string appKey,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var hash = sha256?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(hash))
        {
            throw BusinessException.NotFound(ApiErrorCode.VersionsArtifactNotFound, "文件不存在。");
        }

        var owned = await _db.Set<ReleaseFile>()
            .AsNoTracking()
            .AnyAsync(
                x => x.Sha256 == hash
                     && x.Artifact.Release.AppKey == appKey
                     && x.Artifact.Release.Status == VersionConventions.StatusPublished,
                cancellationToken);
        if (!owned)
        {
            throw BusinessException.NotFound(ApiErrorCode.VersionsArtifactNotFound, "文件不存在或未发布。");
        }

        var blobPath = VersionBlobPaths.Blob(appKey, hash);
        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw BusinessException.NotFound(ApiErrorCode.VersionsArtifactNotFound, "文件内容缺失。");
        }

        var length = await _blobs.GetLengthAsync(blobPath, cancellationToken);
        return new ReleaseContentInfo(hash, length, blobPath);
    }
}
