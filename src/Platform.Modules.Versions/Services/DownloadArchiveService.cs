using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipelines;
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
/// <item>用 Store（不压缩）模式：exe 本身已压缩，重压收益低而 CPU 与耗时高；</item>
/// <item>源 → zip 用管道单遍直写到 Blob 存储，不先落临时文件，避免大包双倍磁盘 IO；</item>
/// <item>条目时间戳固定，重复生成的结果稳定；</item>
/// <item>缓存路径与源同哈希，孤儿扫描按同一 sha 判定引用，源删干净时一起回收。</item>
/// </list>
/// </summary>
public sealed class DownloadArchiveService : IDownloadArchiveService
{
    private const int BufferSize = 81920;

    /// <summary>固定条目时间戳：让同一内容的 zip 字节稳定，便于排查与缓存。</summary>
    private static readonly DateTimeOffset ArchiveTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    private readonly IBlobStorage _blobs;

    /// <summary>构造。</summary>
    public DownloadArchiveService(IBlobStorage blobs)
    {
        _blobs = blobs;
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
        var entryName = SafeEntryName(sourceFileName);
        var archiveName = $"{entryName}.zip";
        var blobPath = VersionBlobPaths.ArchiveZip(appKey, sha256);

        if (await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            return new DownloadArchive(
                blobPath, archiveName, await _blobs.GetLengthAsync(blobPath, cancellationToken));
        }

        // 同一内容并发下载只生成一次；不同内容互不阻塞。
        var gate = Gates.GetOrAdd(blobPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
            {
                await CreateAsync(
                    VersionBlobPaths.Blob(appKey, sha256), blobPath, entryName, cancellationToken);
            }

            return new DownloadArchive(
                blobPath, archiveName, await _blobs.GetLengthAsync(blobPath, cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CreateAsync(
        string sourcePath,
        string blobPath,
        string entryName,
        CancellationToken cancellationToken)
    {
        // 生产者（读源 → 写 zip）与消费者（Blob 存储拉取）通过管道并行，
        // 大文件不整段进内存，也不额外落一份临时 zip。
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        var producer = Task.Run(
            async () =>
            {
                try
                {
                    await using var source = await _blobs.OpenReadAsync(sourcePath, token);
                    await using var zipSink = pipe.Writer.AsStream(leaveOpen: true);
                    using (var archive = new ZipArchive(zipSink, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                        entry.LastWriteTime = ArchiveTimestamp;
                        await using var entryStream = entry.Open();
                        await source.CopyToAsync(entryStream, BufferSize, token);
                    }

                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception exception)
                {
                    // 让消费侧的 ReadAsync 以同一异常结束，双方不会互相等待。
                    try
                    {
                        await pipe.Writer.CompleteAsync(exception);
                    }
                    catch (InvalidOperationException)
                    {
                        // 已经完成（取消路径）：原始异常优先。
                    }

                    throw;
                }
            },
            token);

        try
        {
            await using var zipSource = pipe.Reader.AsStream();
            await _blobs.WriteAsync(blobPath, zipSource, token);
            await producer;
        }
        catch
        {
            // 写存储先失败时让生产者立即退出，避免它卡在管道的写端。
            linked.Cancel();
            try
            {
                await producer;
            }
            catch
            {
                // 原始异常优先。
            }

            throw;
        }
    }

    /// <summary>zip 条目名只保留文件名本身，避免目录穿越与非法字符。</summary>
    private static string SafeEntryName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim();
        return string.IsNullOrEmpty(cleaned) ? "download" : cleaned;
    }
}
