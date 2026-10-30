namespace Platform.Kernel.Abstractions;

/// <summary>
/// 应用键目录：身份模块需要知道"哪些应用存在/启用"，但不得引用 Apps 模块的实体。
/// 由 Apps 模块提供实现，宿主装配；摘除 Apps 时身份模块的应用级角色会明确失败，而不是静默放行。
/// </summary>
public interface IAppDirectory
{
    /// <summary>列出全部启用应用键（平台管理员令牌据此获得应用上下文）。</summary>
    Task<IReadOnlyList<string>> ListEnabledKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>应用键是否存在（无论启用状态）。</summary>
    Task<bool> ExistsAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>确保应用存在（幂等种子使用）；已存在则不改动。</summary>
    Task EnsureAsync(string appKey, string name, CancellationToken cancellationToken = default);
}
