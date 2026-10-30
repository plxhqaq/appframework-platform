namespace Platform.Workers.Retention;

/// <summary>保留策略清理任务配置（<c>Workers:Retention</c> 节点）。</summary>
public sealed class RetentionOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Workers:Retention";

    /// <summary>扫描间隔（秒）。</summary>
    public int IntervalSeconds { get; set; } = 600;
}
