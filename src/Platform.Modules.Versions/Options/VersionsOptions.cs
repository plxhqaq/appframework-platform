using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Options;

/// <summary>版本模块配置。</summary>
public sealed class VersionsOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Versions";

    /// <summary>上传票据有效期（分钟）。</summary>
    public int UploadTicketMinutes { get; set; } = VersionConventions.UploadTicketMinutes;

    /// <summary>单个文件大小上限（字节）。</summary>
    public long MaxFileSizeBytes { get; set; } = VersionConventions.MaxFileSizeBytes;

    /// <summary>单个增量包大小上限（字节）；票据上限取它与文件上限的较大值。</summary>
    public long MaxPackageSizeBytes { get; set; } = VersionConventions.MaxPackageSizeBytes;

    /// <summary>单个产物的文件数上限。</summary>
    public int MaxFilesPerArtifact { get; set; } = VersionConventions.MaxFilesPerArtifact;

    /// <summary>单个增量包解压后的总字节上限（默认 20 GiB）。</summary>
    public long MaxExtractedBytesPerPackage { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>单个 zip 的条目数上限（默认 100 000）。</summary>
    public int MaxZipEntries { get; set; } = 100_000;

    /// <summary>zip 压缩比上限（解压总量 / 压缩包大小，默认 200）。</summary>
    public int MaxCompressionRatio { get; set; } = 200;

    /// <summary>摄取前要求的最小剩余磁盘空间（字节，默认 5 GiB）。</summary>
    public long MinFreeDiskBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>摄取完成后是否保留原始包文件（默认保留，便于重放与审计）。</summary>
    public bool RetainRawPackage { get; set; } = true;

    /// <summary>是否强制包文件名匹配 <see cref="PackageFileNamePatterns"/>（默认只警告）。</summary>
    public bool RequirePackageFileNameMatch { get; set; }

    /// <summary>
    /// 合法包文件名模板（CSV）。用字符串而不是数组，环境变量（Versions__PackageFileNamePatterns）
    /// 才能直接整串注入；占位符：{channel} / {version} / {kind} / {ext}。
    /// </summary>
    public string PackageFileNamePatterns { get; set; } =
        "Setup-{channel}-{version}{ext},Update-{channel}-{version}{ext},AppFramework.Updater-{version}{ext}";

    /// <summary>增量包摄取 Worker 的并发数（默认 1）。</summary>
    public int IngestionConcurrency { get; set; } = 1;

    /// <summary>摄取任务认领租约（分钟，默认 10）。</summary>
    public int IngestionLockMinutes { get; set; } = 10;

    /// <summary>是否启用摄取后台循环（测试可关掉，改由用例直接驱动）。</summary>
    public bool IngestionEnabled { get; set; } = true;

    /// <summary>摄取轮询间隔（秒，默认 10）。</summary>
    public int IngestionPollSeconds { get; set; } = 10;

    /// <summary>可重试错误的最大尝试次数（默认 3）；超过后置 failed。</summary>
    public int IngestionMaxAttempts { get; set; } = 3;

    /// <summary>是否启用孤儿 Blob 扫描（默认启用；测试或只读副本可关闭）。</summary>
    public bool BlobSweepEnabled { get; set; } = true;

    /// <summary>
    /// 孤儿 Blob 只报告不删除（默认 <c>true</c>）。生产先观察一周，
    /// 确认候选与实际引用一致后再改为 <c>false</c> 正式回收。
    /// </summary>
    public bool BlobSweepDryRun { get; set; } = true;

    /// <summary>孤儿判定宽限期（小时，默认 48）；不得小于上传票据有效期加余量。</summary>
    public int BlobSweepGraceHours { get; set; } = 48;

    /// <summary>孤儿扫描每批处理的候选数（默认 500），用于限制单轮内存与删除突发。</summary>
    public int BlobSweepBatchSize { get; set; } = 500;

    /// <summary>失败包保留天数（默认 30）；到期先删数据库行，Blob 交给孤儿扫描回收。</summary>
    public int FailedPackageRetentionDays { get; set; } = 30;

    /// <summary>已归档版本保留天数（默认 0 = 永久保留）；到期删除版本及其清单/包行。</summary>
    public int ArchivedReleaseRetentionDays { get; set; }
}
