namespace Platform.Kernel.Abstractions;

/// <summary>
/// Blob 存储抽象（大文件：版本产物、文档、超大备份）。
/// </summary>
/// <remarks>
/// 实现必须满足**发布不变式**：先写临时文件 → 由调用方提交数据库 → rename 到最终路径。
/// 这样任何中途失败都不会留下"库里有一行、磁盘上没文件"的半成品。
/// 相对路径由平台生成（按 <c>app/{appKey}/...</c> 分桶），调用方不得传绝对路径。
/// </remarks>
public interface IBlobStorage
{
    /// <summary>文件是否存在。</summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>文件长度（字节）；不存在抛 <see cref="FileNotFoundException"/>。</summary>
    Task<long> GetLengthAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>顺序读全文。</summary>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>区间读（HTTP Range 续传用）。</summary>
    Task<Stream> OpenRangeAsync(
        string relativePath,
        long offset,
        long? count = null,
        CancellationToken cancellationToken = default);

    /// <summary>写入（边写边算 sha256 与长度），返回实际落盘信息。</summary>
    Task<BlobWriteResult> WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入"待发布"文件（最终路径 + <c>.pending</c>）：先落盘，**调用方提交数据库后再发布**。
    /// 用于需要"临时文件 → 库提交 → rename"三段式不变式的场景（备份、文档）。
    /// </summary>
    Task<BlobWriteResult> WritePendingAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>把待发布文件 rename 到最终路径；待发布文件不存在时抛 <see cref="FileNotFoundException"/>。</summary>
    Task PublishAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>丢弃待发布文件（数据库提交失败时的补偿动作）。</summary>
    Task DiscardPendingAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>删除（不存在视为成功）。</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>落盘结果。</summary>
/// <param name="RelativePath">相对路径。</param>
/// <param name="SizeBytes">字节数。</param>
/// <param name="Sha256">小写十六进制 SHA-256。</param>
public readonly record struct BlobWriteResult(string RelativePath, long SizeBytes, string Sha256);
