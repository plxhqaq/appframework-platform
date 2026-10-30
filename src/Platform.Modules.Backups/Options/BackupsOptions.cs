using Platform.Contracts.Backups;

namespace Platform.Modules.Backups.Options;

/// <summary>备份模块配置。</summary>
public sealed class BackupsOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Backups";

    /// <summary>内联阈值（字节）；超过转存 Blob。</summary>
    public int InlineMaxBytes { get; set; } = BackupConventions.InlineMaxBytes;

    /// <summary>单个请求负载上限（字节）；防止超大 JSON 把请求体压垮。</summary>
    public long MaxPayloadBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>下发等待回执的超时（秒）。</summary>
    public int PushAckTimeoutSeconds { get; set; } = 60;

    /// <summary>软删除到物理清理的宽限期（分钟）。</summary>
    public int CleanupGraceMinutes { get; set; } = 30;

    /// <summary>命名空间默认保留版本数。</summary>
    public int DefaultMaxVersions { get; set; } = BackupConventions.DefaultMaxVersions;
}
