namespace Platform.Kernel.Abstractions;

/// <summary>Blob 枚举条目：只暴露相对路径、长度与最后写入时间，不泄露物理根目录。</summary>
/// <param name="RelativePath">相对存储根的路径（正斜杠）。</param>
/// <param name="SizeBytes">文件字节数。</param>
/// <param name="LastModifiedAt">最后写入时间（UTC）。</param>
public readonly record struct BlobEntry(string RelativePath, long SizeBytes, DateTimeOffset LastModifiedAt);

/// <summary>
/// Blob 维护侧枚举能力（孤儿清理、盘点）。与 <see cref="IBlobStorage"/> 分开：
/// 读写实现不必为了业务被迫实现枚举，未来不支持列举的对象存储也可以不注册本接口。
/// </summary>
public interface IBlobEnumerator
{
    /// <summary>递归枚举指定前缀下的文件；前缀不存在时返回空序列。</summary>
    /// <param name="prefix">相对路径前缀（例如 <c>versions/</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    IAsyncEnumerable<BlobEntry> EnumerateAsync(string prefix, CancellationToken cancellationToken = default);
}
