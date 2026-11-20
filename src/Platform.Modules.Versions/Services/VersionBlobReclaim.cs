using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Packages;

namespace Platform.Modules.Versions.Services;

/// <summary>
/// 内容寻址回收：删除登记行后，按 sha 判定是否仍被任何应用的四类内容行引用，
/// 无引用才删源文件与下载派生缓存；与孤儿扫描同口径（见 VersionsRetentionTask）。
/// </summary>
internal static class VersionBlobReclaim
{
    public static async Task DeleteUnreferencedAsync(
        PlatformDbContext db,
        IBlobStorage blobs,
        string appKey,
        List<string> shas,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        for (var offset = 0; offset < shas.Count; offset += batchSize)
        {
            var chunk = shas.GetRange(offset, Math.Min(batchSize, shas.Count - offset));

            // 引用判定与孤儿扫描同口径：可能来自任何应用，四类行都算引用。
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            referenced.UnionWith(await db.Set<ReleaseFile>()
                .Where(x => chunk.Contains(x.Sha256))
                .Select(static x => x.Sha256)
                .Distinct()
                .ToListAsync(cancellationToken));
            referenced.UnionWith(await db.Set<ReleasePackage>()
                .Where(x => chunk.Contains(x.Sha256))
                .Select(static x => x.Sha256)
                .Distinct()
                .ToListAsync(cancellationToken));
            referenced.UnionWith(await db.Set<AppUpdaterRelease>()
                .Where(x => chunk.Contains(x.Sha256))
                .Select(static x => x.Sha256)
                .Distinct()
                .ToListAsync(cancellationToken));
            referenced.UnionWith(await db.Set<DriverMedia>()
                .Where(x => chunk.Contains(x.Sha256))
                .Select(static x => x.Sha256)
                .Distinct()
                .ToListAsync(cancellationToken));

            foreach (var sha in chunk.Where(x => !referenced.Contains(x)))
            {
                await blobs.DeleteAsync(VersionBlobPaths.Blob(appKey, sha), cancellationToken);
                await blobs.DeleteAsync(VersionBlobPaths.ArchiveZip(appKey, sha), cancellationToken);
            }
        }
    }
}
