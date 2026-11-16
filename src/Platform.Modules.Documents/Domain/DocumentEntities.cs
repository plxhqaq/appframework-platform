using System.Diagnostics.CodeAnalysis;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Documents.Domain;

/// <summary>文档集合：一类文档的分组。</summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "文档集合是领域分组概念，不是 .NET 集合类型。")]
public sealed class DocumentCollection : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键（隔离轴）。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>集合键（应用内唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用；停用后客户端清单不再返回该集合。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>集合内文档。</summary>
    public List<Document> Documents { get; set; } = [];
}

/// <summary>
/// 文档逻辑主体：指向最新修订，并承载可见性。删除为软删除，
/// 修订记录与物理文件由后台任务在宽限期后清理。
/// </summary>
public sealed class Document : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属集合。</summary>
    public Guid CollectionId { get; set; }

    /// <summary>所属集合导航。</summary>
    public DocumentCollection Collection { get; set; } = null!;

    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>可见性：client / internal。客户端接口一律按此服务端过滤。</summary>
    public string Visibility { get; set; } = "internal";

    /// <summary>适用版本（如客户端框架版本），可空；仅元数据，不参与可见性判定。</summary>
    public string? ApplicableVersion { get; set; }

    /// <summary>最新修订号。</summary>
    public int Revision { get; set; }

    /// <summary>最新文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>最新内容类型。</summary>
    public string? ContentType { get; set; }

    /// <summary>最新内容 Blob 相对路径。</summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>最新内容字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>最新内容 SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>创建/上传主体。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>软删除时间。</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>修订历史。</summary>
    public List<DocumentRevision> Revisions { get; set; } = [];
}

/// <summary>文档修订：每次上传一条，旧修订保留，可下载与追溯。</summary>
public sealed class DocumentRevision : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属文档。</summary>
    public Guid DocumentId { get; set; }

    /// <summary>所属文档导航。</summary>
    public Document Document { get; set; } = null!;

    /// <summary>修订号（文档内从 1 递增）。</summary>
    public int Revision { get; set; }

    /// <summary>文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>内容类型。</summary>
    public string? ContentType { get; set; }

    /// <summary>Blob 相对路径。</summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>上传主体。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>随文档软删除时同步标记，供后台清理。</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
