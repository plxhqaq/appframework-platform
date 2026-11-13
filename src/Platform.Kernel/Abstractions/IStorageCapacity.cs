namespace Platform.Kernel.Abstractions;

/// <summary>存储容量探测（上传票据与摄取前的磁盘保护）。</summary>
public interface IStorageCapacity
{
    /// <summary>
    /// Blob 根所在卷的可用字节数。无法判定时返回 <see cref="long.MaxValue"/>，
    /// 由调用方的写入失败兜底，避免探测能力缺失导致误拒。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<long> GetAvailableFreeBytesAsync(CancellationToken cancellationToken = default);
}
