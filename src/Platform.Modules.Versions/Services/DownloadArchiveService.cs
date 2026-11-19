using Platform.Kernel.Abstractions;

namespace Platform.Modules.Versions.Services;

/// <summary>
/// 管理台下载的 zip 表现层：非 zip 源（安装包 / 更新器的 exe）按内容哈希缓存一份 zip，
/// 之后的下载与断点续传都直接读缓存文件。
/// </summary>
public interface IDownloadArchiveService
{
    /// <summary>该文件名是否需要打成 zip（只有 .zip 原样下发）。</summary>
    bool NeedsArchive(string fileName);

    /// <summary>确保 zip 缓存存在并返回其位置与下发文件名。</summary>
    Task<DownloadArchive> EnsureAsync(
        string appKey,
        string sha256,
        string sourceFileName,
        CancellationToken cancellationToken = default);
}

/// <summary>zip 缓存信息。</summary>
public sealed record DownloadArchive(string BlobPath, string FileName, long SizeBytes);

/// <summary>
/// 实现要点：
/// <list type="bullet">
/// <item>单源打包委托给 <see cref="IZipComposer"/>（Store 模式、固定时间戳、管道单遍直写）；</item>
/// <item>缓存路径与源同哈希，孤儿扫描按同一 sha 判定引用，源删干净时一起回收。</item>
/// </list>
/// </summary>
public sealed class DownloadArchiveService : IDownloadArchiveService
{
    private readonly IBlobStorage _blobs;
    private readonly IZipComposer _composer;

    /// <summary>构造。</summary>
    public DownloadArchiveService(IBlobStorage blobs, IZipComposer composer)
    {
        _blobs = blobs;
        _composer = composer;
    }

    /// <inheritdoc />
    public bool NeedsArchive(string fileName) =>
        !string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DownloadArchive> EnsureAsync(
        string appKey,
        string sha256,
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        var entryName = ZipComposer.SafeEntryName(sourceFileName);
        var archiveName = $"{entryName}.zip";
        var blobPath = VersionBlobPaths.ArchiveZip(appKey, sha256);

        if (await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            return new DownloadArchive(
                blobPath, archiveName, await _blobs.GetLengthAsync(blobPath, cancellationToken));
        }

        // 同一内容并发下载只生成一次；不同内容互不阻塞（门闸在 ZipComposer 内按目标路径加）。
        await _composer.ComposeAsync(
            blobPath,
            [
                new ZipCompositionItem(
                    entryName, ZipItemKind.WholeBlob, SourceBlobPath: VersionBlobPaths.Blob(appKey, sha256)),
            ],
            cancellationToken);

        return new DownloadArchive(
            blobPath, archiveName, await _blobs.GetLengthAsync(blobPath, cancellationToken));
    }
}
