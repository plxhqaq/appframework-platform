using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Storage;

/// <summary>本地文件存储配置（对象存储接入前的默认实现）。</summary>
public sealed class BlobStorageOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "BlobStorage";

    /// <summary>根目录（相对路径按 <see cref="AppContext.BaseDirectory"/> 解析）。</summary>
    public string RootPath { get; set; } = "data/blobs";

    /// <summary>临时目录名（相对根目录）。写入先落临时再 rename，保证不出现半个文件。</summary>
    public string TempDirectoryName { get; set; } = ".tmp";

    /// <summary>读写缓冲区大小。</summary>
    public int BufferSize { get; set; } = 81920;
}

/// <summary>
/// 本地磁盘 Blob 存储。
/// </summary>
/// <remarks>
/// 实现"临时文件 → rename 发布"不变式：写入全程落 <c>.tmp</c>，成功后
/// <see cref="File.Move(string, string, bool)"/> 原子替换。任何中途失败都不会留下可被读到的半成品。
/// 后续接入 S3/MinIO 时只需替换本类实现，接口不变。
/// </remarks>
public sealed class LocalBlobStorage : IBlobStorage
{
    private readonly BlobStorageOptions _options;
    private readonly string _root;

    /// <summary>构造并确保根目录存在。</summary>
    public LocalBlobStorage(IOptions<BlobStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _root = Path.GetFullPath(_options.RootPath, AppContext.BaseDirectory);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));

    /// <inheritdoc />
    public Task<long> GetLengthAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Blob 不存在。", relativePath);
        }
        return Task.FromResult(new FileInfo(path).Length);
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Blob 不存在。", relativePath);
        }
        return Task.FromResult<Stream>(OpenFile(path, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    /// <inheritdoc />
    public Task<Stream> OpenRangeAsync(
        string relativePath,
        long offset,
        long? count = null,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Blob 不存在。", relativePath);
        }

        var length = new FileInfo(path).Length;
        if (offset < 0 || offset > length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "区间起点超出文件长度。");
        }

        var stream = OpenFile(path, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        var available = length - offset;
        var bounded = count is > 0 ? Math.Min(count.Value, available) : available;
        return Task.FromResult<Stream>(new BoundedReadStream(stream, bounded));
    }

    /// <inheritdoc />
    public async Task<BlobWriteResult> WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var target = Resolve(relativePath);
        var targetDirectory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempDirectory = Path.Combine(_root, _options.TempDirectoryName);
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.part");

        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var buffer = new byte[Math.Max(4096, _options.BufferSize)];
            await using (var file = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             buffer.Length,
                             FileOptions.Asynchronous))
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                }
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            File.Move(tempPath, target, overwrite: true);
            return new BlobWriteResult(relativePath, size, sha256);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        TryDelete(Resolve(relativePath));
        return Task.CompletedTask;
    }

    private static FileStream OpenFile(string path, FileOptions options) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, options);

    /// <summary>把相对路径解析到根目录下，并阻止 <c>..</c> 越界。</summary>
    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("相对路径不能为空。", nameof(relativePath));
        }

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"路径超出存储根目录：{relativePath}", nameof(relativePath));
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 被占用：留待后台清理，不阻断业务
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足：同上
        }
    }
}

/// <summary>限定最多读取 N 字节的只读包装流（HTTP Range 响应用）。</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _consumed;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length < 0 ? 0 : length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_consumed >= _length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, _length - _consumed);
        var read = _inner.Read(buffer, offset, toRead);
        _consumed += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_consumed >= _length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _length - _consumed);
        var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        _consumed += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
