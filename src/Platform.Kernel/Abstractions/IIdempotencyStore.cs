namespace Platform.Kernel.Abstractions;

/// <summary>
/// 幂等存储：创建类 POST 携带 <c>Idempotency-Key</c> 时，同一键在 TTL 内只允许执行一次，
/// 重复请求返回首次结果而不是重复建资源。
/// </summary>
/// <remarks>
/// 默认实现走数据库表（`idempotency_records`），不放在进程内存——多副本部署时内存去重无效。
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>尝试占位。返回 <c>true</c> 表示抢到执行权；<c>false</c> 表示同键已在处理/已完成。</summary>
    Task<bool> TryReserveAsync(string scope, string key, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>读取首次结果（尚未完成时返回 <c>default</c>）。</summary>
    Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>保存首次结果。</summary>
    Task SaveAsync<T>(string scope, string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>释放占位（执行失败时调用，允许客户端重试）。</summary>
    Task ReleaseAsync(string scope, string key, CancellationToken cancellationToken = default);
}
