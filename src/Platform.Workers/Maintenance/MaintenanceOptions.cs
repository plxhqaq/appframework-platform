namespace Platform.Workers.Maintenance;

/// <summary>后台维护任务配置（<c>Workers:Maintenance</c> 节点）。</summary>
public sealed class MaintenanceOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Workers:Maintenance";

    /// <summary>扫描间隔（秒）。</summary>
    public int IntervalSeconds { get; set; } = 600;

    /// <summary>幂等记录保留时长（小时）；过期后删除，释放唯一索引空间。</summary>
    public int IdempotencyRetentionHours { get; set; } = 24;

    /// <summary>审计日志保留天数。</summary>
    public int AuditRetentionDays { get; set; } = 180;

    /// <summary>单次清理的最大行数，避免一次删太多拖长事务。</summary>
    public int BatchSize { get; set; } = 5000;
}
