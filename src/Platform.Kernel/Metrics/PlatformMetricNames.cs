namespace Platform.Kernel.Metrics;

/// <summary>
/// 指标名与说明的唯一来源。
/// </summary>
/// <remarks>
/// 渲染时以首次注册的 help/标签顺序为准，同名不同标签集会写出错误的时间序列，
/// 所以新增维度只改这里，不要在调用点临时拼名。命名遵循 Prometheus 约定：
/// 名带单位后缀（<c>_seconds</c> / <c>_total</c>），标签值不做高基数（禁止用 id、traceId）。
/// </remarks>
public static class PlatformMetricNames
{
    // ━━━ 进程 ━━━
    public const string ProcessStartTime = "platform_process_start_time_seconds";
    public const string ProcessUptime = "platform_process_uptime_seconds";
    public const string ProcessMemory = "platform_process_memory_bytes";
    public const string ProcessGcCollections = "platform_process_gc_collections";
    public const string ProcessThreads = "platform_process_thread_pool_size";
    public const string HttpRequestDuration = "platform_http_request_duration_seconds";

    // ━━━ 横切失败 ━━━
    public const string AuditWriteFailures = "platform_audit_write_failures_total";
    public const string MetricSourceErrors = "platform_metric_source_errors_total";

    /// <summary>进程启动时刻（Unix 秒），采集端用它识别副本重启。</summary>
    public const string ProcessStartTimeHelp = "进程启动时间（Unix 秒）。";

    public const string ProcessUptimeHelp = "进程已运行秒数。";

    public const string ProcessMemoryHelp = "托管堆已分配字节数。";

    public const string ProcessGcCollectionsHelp = "各代 GC 回收次数。";

    public const string ProcessThreadsHelp = "线程池当前工作线程与完成端口线程数。";

    public const string AuditWriteFailuresHelp = "审计写入失败次数（失败只记日志，不阻断业务）。";

    public const string MetricSourceErrorsHelp = "抓取指标时业务数据源采集失败次数。";
}
