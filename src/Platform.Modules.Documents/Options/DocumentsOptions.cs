using Platform.Contracts.Documents;

namespace Platform.Modules.Documents.Options;

/// <summary>文档模块配置。</summary>
public sealed class DocumentsOptions
{
    /// <summary>配置节点名。</summary>
    public const string SectionName = "Documents";

    /// <summary>单个文件大小上限（字节）。</summary>
    public long MaxFileSizeBytes { get; set; } = DocumentConventions.MaxFileSizeBytes;

    /// <summary>软删除到物理清理的宽限期（分钟）。</summary>
    public int CleanupGraceMinutes { get; set; } = 30;
}
