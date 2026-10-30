using Platform.Kernel.Abstractions;

namespace Platform.Modules.Backups.Domain;

/// <summary>备份命名空间：同一应用下的配置域，承载渠道与保留配额。</summary>
public sealed class BackupNamespace : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键（隔离轴，来自令牌而不是请求参数）。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>命名空间键（应用内唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>兼容渠道：不同渠道的备份不允许互相下发（强制门禁）。</summary>
    public string Channel { get; set; } = "default";

    /// <summary>保留版本数：超出后拒绝新备份，直到删除旧版本。</summary>
    public int MaxVersions { get; set; } = 50;

    /// <summary>是否启用；停用后不再接收新备份。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该命名空间下的备份。</summary>
    public List<Backup> Backups { get; set; } = [];
}

/// <summary>
/// 备份。负载 ≤64KB 直接存 <see cref="PayloadJson"/>；超过则转存 Blob 并记录哈希与长度。
/// 删除只打 <see cref="DeletedAt"/>，物理文件由后台清理任务在宽限期后先删文件再删行。
/// </summary>
public sealed class Backup : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属命名空间。</summary>
    public Guid NamespaceId { get; set; }

    /// <summary>所属命名空间导航。</summary>
    public BackupNamespace Namespace { get; set; } = null!;

    /// <summary>类型：config / profile。</summary>
    public string Kind { get; set; } = "config";

    /// <summary>备份名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>来源设备；为空表示全局备份。</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>来源设备名（列表展示用，设备表在客户端侧）。</summary>
    public string? DeviceName { get; set; }

    /// <summary>模块标识（可选）。</summary>
    public string? ModuleId { get; set; }

    /// <summary>负载 schema 版本。</summary>
    public int SchemaVersion { get; set; }

    /// <summary>产生备份的框架版本。</summary>
    public string FrameworkVersion { get; set; } = string.Empty;

    /// <summary>渠道（归一后小写；与命名空间渠道做硬门禁）。</summary>
    public string Channel { get; set; } = "default";

    /// <summary>内联负载（≤64KB 时非空）。</summary>
    public string? PayloadJson { get; set; }

    /// <summary>转存文件名（客户端建议名）。</summary>
    public string? FileName { get; set; }

    /// <summary>转存 Blob 相对路径（超过 64KB 时非空）。</summary>
    public string? BlobPath { get; set; }

    /// <summary>负载 SHA-256（小写十六进制）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>负载字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>软删除时间；后台清理后整行删除。</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
