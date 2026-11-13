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

    // ━━━ 后台维护 ━━━
    public const string RetentionDeletedRows = "platform_retention_deleted_rows_total";
    public const string RetentionDeletedFiles = "platform_retention_deleted_files_total";

    // ━━━ 版本与发布包 ━━━
    public const string VersionsPackageProbe = "versions_package_probe_total";
    public const string VersionsPackageRegister = "versions_package_register_total";
    public const string VersionsPackageIngest = "versions_package_ingest_total";
    public const string VersionsPackageIngestSeconds = "versions_package_ingest_seconds";
    public const string VersionsPackageBytes = "versions_package_bytes_total";
    public const string VersionsPackageFiles = "versions_package_files";
    public const string VersionsCheck = "versions_check_total";
    public const string VersionsPackageDownloadBytes = "versions_package_download_bytes_total";
    public const string VersionsUploadTicketsExpired = "versions_upload_tickets_expired_total";
    public const string VersionsBlobOrphansScanned = "versions_blob_orphans_scanned_total";
    public const string VersionsBlobOrphansDeleted = "versions_blob_orphans_deleted_total";
    public const string VersionsBlobOrphansDeletedBytes = "versions_blob_orphans_deleted_bytes_total";

    // ━━━ 横切失败 ━━━
    public const string AuditWriteFailures = "platform_audit_write_failures_total";
    public const string MetricSourceErrors = "platform_metric_source_errors_total";

    // ━━━ 版本与发布包（help） ━━━
    public const string VersionsPackageProbeHelp = "发布包清单探测次数，按格式与结果分组。";
    public const string VersionsPackageRegisterHelp = "发布包/更新器登记次数，按类型与结果分组。";
    public const string VersionsPackageIngestHelp = "增量包摄取次数，按结果分组。";
    public const string VersionsPackageIngestSecondsHelp = "增量包摄取耗时（秒）。";
    public const string VersionsPackageBytesHelp = "登记成功的包字节数，按包类型分组。";
    public const string VersionsPackageFilesHelp = "最近一次成功摄取的清单文件数，按包类型分组。";
    public const string VersionsCheckHelp = "客户端检查更新次数，按结果分组。";
    public const string VersionsPackageDownloadBytesHelp = "版本内容下载字节数，按内容类型（file/package/updater）分组。";
    public const string VersionsUploadTicketsExpiredHelp = "保留期清理删除的过期上传票据数。";
    public const string VersionsBlobOrphansScannedHelp = "孤儿 Blob 扫描命中的候选文件数（含 dry-run）。";
    public const string VersionsBlobOrphansDeletedHelp = "孤儿 Blob 实际删除文件数。";
    public const string VersionsBlobOrphansDeletedBytesHelp = "孤儿 Blob 实际删除字节数。";

    /// <summary>进程启动时刻（Unix 秒），采集端用它识别副本重启。</summary>
    public const string ProcessStartTimeHelp = "进程启动时间（Unix 秒）。";

    public const string ProcessUptimeHelp = "进程已运行秒数。";

    public const string ProcessMemoryHelp = "托管堆已分配字节数。";

    public const string ProcessGcCollectionsHelp = "各代 GC 回收次数。";

    public const string ProcessThreadsHelp = "线程池当前工作线程与完成端口线程数。";

    public const string AuditWriteFailuresHelp = "审计写入失败次数（失败只记日志，不阻断业务）。";

    public const string MetricSourceErrorsHelp = "抓取指标时业务数据源采集失败次数。";

    public const string RetentionDeletedRowsHelp = "保留策略清理删除的数据库行数，按任务分组。";

    public const string RetentionDeletedFilesHelp = "保留策略清理删除的物理文件数，按任务分组。";
}
