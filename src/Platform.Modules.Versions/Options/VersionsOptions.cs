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

    /// <summary>单个产物的文件数上限。</summary>
    public int MaxFilesPerArtifact { get; set; } = VersionConventions.MaxFilesPerArtifact;
}
