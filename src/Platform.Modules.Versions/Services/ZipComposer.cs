using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipelines;
using Platform.Contracts;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;

namespace Platform.Modules.Versions.Services;

/// <summary>组合 zip 的单个条目来源。</summary>
public enum ZipItemKind
{
    /// <summary>把一个 Blob 整体作为一条根条目写入（不解压），如全量包 exe。</summary>
    WholeBlob,

    /// <summary>从源 zip Blob 中抽取指定条目原样搬运，如媒体 zip 里的 media/** 文件。</summary>
    ExtractEntry,

    /// <summary>写入内存中生成的文本字节，如 <c>media/media-order.json</c>。</summary>
    InlineText,
}

/// <summary>
/// 组合 zip 的一条输出条目。
/// <list type="bullet">
/// <item><see cref="ZipItemKind.WholeBlob"/>：读 <see cref="SourceBlobPath"/> 全部字节，写成 <see cref="EntryName"/>；</item>
/// <item><see cref="ZipItemKind.ExtractEntry"/>：打开 <see cref="SourceBlobPath"/>（zip），搬运其中 <see cref="SourceEntryName"/> 条目到 <see cref="EntryName"/>；</item>
/// <item><see cref="ZipItemKind.InlineText"/>：把 <see cref="InlineText"/>（UTF-8）写成 <see cref="EntryName"/>。</item>
/// </list>
/// </summary>
public sealed record ZipCompositionItem(
    string EntryName,
    ZipItemKind Kind,
    string? SourceBlobPath = null,
    string? SourceEntryName = null,
    string? InlineText = null);

/// <summary>多源确定性 zip 合成器。</summary>
public interface IZipComposer
{
    /// <summary>
    /// 按给定顺序把多源条目合成为一个 zip 并落盘到 <paramref name="targetBlobPath"/>，返回写入字节数。
    /// 结果确定性：条目顺序由调用方给定、时间戳固定、不压缩；同输入必得同字节。
    /// </summary>
    Task<ZipComposeResult> ComposeAsync(
        string targetBlobPath,
        IReadOnlyList<ZipCompositionItem> items,
        CancellationToken cancellationToken = default);
}

/// <summary>合成结果：目标路径、落盘后长度、条目数。</summary>
public sealed record ZipComposeResult(string BlobPath, long SizeBytes, int EntryCount);

/// <summary>
/// 实现要点（沿用 <see cref="DownloadArchiveService"/> 已验证的流式模式）：
/// <list type="bullet">
/// <item>Store（不压缩）：媒体与 exe 本身已压缩，重压收益低；</item>
/// <item>生产者（读多源 → 写 zip）与消费者（Blob 存储拉取）用管道并行，全程不落地临时 zip；</item>
/// <item>条目时间戳固定为 2000-01-01，同组合字节稳定；</item>
/// <item>目标按 blobPath 加进程内门闸，并发首次合成只算一次。</item>
/// </list>
/// </summary>
public sealed class ZipComposer : IZipComposer
{
    private const int BufferSize = 81920;

    /// <summary>固定条目时间戳：让同一组合的 zip 字节稳定。</summary>
    private static readonly DateTimeOffset ArchiveTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    private readonly IBlobStorage _blobs;

    /// <summary>构造。</summary>
    public ZipComposer(IBlobStorage blobs)
    {
        _blobs = blobs;
    }

    /// <inheritdoc />
    public async Task<ZipComposeResult> ComposeAsync(
        string targetBlobPath,
        IReadOnlyList<ZipCompositionItem> items,
        CancellationToken cancellationToken = default)
    {
        ValidateEntryNames(items);

        var gate = Gates.GetOrAdd(targetBlobPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 门闸内再查一次：命中已存在的缓存则直接复用，不重复合成。
            if (await _blobs.ExistsAsync(targetBlobPath, cancellationToken))
            {
                return new ZipComposeResult(
                    targetBlobPath,
                    await _blobs.GetLengthAsync(targetBlobPath, cancellationToken),
                    items.Count);
            }

            await CreateAsync(targetBlobPath, items, cancellationToken);
            return new ZipComposeResult(
                targetBlobPath,
                await _blobs.GetLengthAsync(targetBlobPath, cancellationToken),
                items.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>条目名必须非空且不重复（Windows 解压器大小写不敏感，故按忽略大小写判重）。</summary>
    private static void ValidateEntryNames(IReadOnlyList<ZipCompositionItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.EntryName))
            {
                throw BusinessException.Invalid("组合 zip 条目名不能为空。");
            }

            if (!seen.Add(item.EntryName))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsBundlePathConflict,
                    $"组合 zip 条目名冲突：{item.EntryName}");
            }
        }
    }

    private async Task CreateAsync(
        string targetBlobPath,
        IReadOnlyList<ZipCompositionItem> items,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        var producer = Task.Run(
            async () =>
            {
                // 同一媒体 zip 只打开一次，供其多条 ExtractEntry 复用。
                var openZips =
                    new Dictionary<string, (Stream Base, ZipArchive Archive)>(StringComparer.Ordinal);
                try
                {
                    await using var zipSink = pipe.Writer.AsStream(leaveOpen: true);
                    using (var archive = new ZipArchive(zipSink, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var item in items)
                        {
                            var entry = archive.CreateEntry(item.EntryName, CompressionLevel.NoCompression);
                            entry.LastWriteTime = ArchiveTimestamp;
                            await using var entryStream = entry.Open();
                            await WriteEntryAsync(entryStream, item, openZips, token);
                        }
                    }

                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception exception)
                {
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
                finally
                {
                    foreach (var (_, pair) in openZips)
                    {
                        pair.Archive.Dispose();
                        await pair.Base.DisposeAsync();
                    }
                }
            },
            token);

        try
        {
            await using var zipSource = pipe.Reader.AsStream();
            await _blobs.WriteAsync(targetBlobPath, zipSource, token);
            await producer;
        }
        catch
        {
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

    private async Task WriteEntryAsync(
        Stream entryStream,
        ZipCompositionItem item,
        Dictionary<string, (Stream Base, ZipArchive Archive)> openZips,
        CancellationToken token)
    {
        switch (item.Kind)
        {
            case ZipItemKind.WholeBlob:
            {
                await using var source = await _blobs.OpenReadAsync(
                    RequireSource(item, nameof(ZipItemKind.WholeBlob)), token);
                await source.CopyToAsync(entryStream, BufferSize, token);
                break;
            }

            case ZipItemKind.ExtractEntry:
            {
                var blobPath = RequireSource(item, nameof(ZipItemKind.ExtractEntry));
                if (!openZips.TryGetValue(blobPath, out var pair))
                {
                    var baseStream = await _blobs.OpenReadAsync(blobPath, token);
                    try
                    {
                        pair = (baseStream, new ZipArchive(baseStream, ZipArchiveMode.Read, leaveOpen: false));
                    }
                    catch
                    {
                        await baseStream.DisposeAsync();
                        throw;
                    }

                    openZips[blobPath] = pair;
                }

                var sourceEntry = pair.Archive.GetEntry(item.SourceEntryName!);
                if (sourceEntry is null)
                {
                    throw new BusinessException(
                        ApiErrorCode.VersionsMediaManifestInvalid,
                        $"媒体 zip 缺少声明条目：{item.SourceEntryName}");
                }

                await using var inEntry = sourceEntry.Open();
                await inEntry.CopyToAsync(entryStream, BufferSize, token);
                break;
            }

            case ZipItemKind.InlineText:
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(item.InlineText ?? string.Empty);
                await entryStream.WriteAsync(bytes, token);
                break;
            }

            default:
                throw BusinessException.Invalid($"未知组合条目来源：{item.Kind}");
        }
    }

    private static string RequireSource(ZipCompositionItem item, string kind)
    {
        if (string.IsNullOrWhiteSpace(item.SourceBlobPath))
        {
            throw BusinessException.Invalid($"{kind} 组合条目缺少源 Blob 路径。");
        }

        return item.SourceBlobPath;
    }

    /// <summary>zip 条目名只保留文件名本身，避免目录穿越与非法字符。</summary>
    public static string SafeEntryName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim();
        return string.IsNullOrEmpty(cleaned) ? "download" : cleaned;
    }
}
