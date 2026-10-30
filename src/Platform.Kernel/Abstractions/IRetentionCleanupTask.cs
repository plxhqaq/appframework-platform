namespace Platform.Kernel.Abstractions;

/// <summary>
/// 保留期清理任务：软删除数据在宽限期后清除物理文件、再删数据库行。
/// 由后台 Worker 按周期批量调用；实现必须**先删文件后删行**，文件删除失败时保留行等待重试。
/// </summary>
public interface IRetentionCleanupTask
{
    /// <summary>任务名（日志用）。</summary>
    string Name { get; }

    /// <summary>执行一轮清理（有上限的批次）。</summary>
    Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>清理结果。</summary>
/// <param name="RowsDeleted">删除的数据库行数。</param>
/// <param name="FilesDeleted">删除的物理文件数（含不存在视为成功）。</param>
public readonly record struct RetentionCleanupResult(int RowsDeleted, int FilesDeleted);
