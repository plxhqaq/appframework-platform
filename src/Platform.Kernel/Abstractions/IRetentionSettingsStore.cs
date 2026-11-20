namespace Platform.Kernel.Abstractions;

/// <summary>
/// 清理宽限期配置（库内覆盖出厂默认）。读取返回 <c>null</c> 时调用方回落各自 <c>*Options</c>；
/// 不做缓存——定时清理每 600s 才读一次，编辑时即时生效更重要。
/// </summary>
public interface IRetentionSettingsStore
{
    /// <summary>读取某个模块的宽限期（分钟）；未配置返回 <c>null</c>。</summary>
    Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default);

    /// <summary>批量写入宽限期；单次 <c>SaveChanges</c>，保证两域不会只改一半。</summary>
    Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default);
}
