namespace Platform.Kernel.Abstractions;

/// <summary>审计字段。所有业务实体实现它，保存时由 DbContext 统一填时间戳（不靠各服务自觉）。</summary>
public interface IAuditableEntity
{
    /// <summary>创建时间（UTC）。</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>最后修改时间（UTC）。</summary>
    DateTimeOffset UpdatedAt { get; set; }
}
