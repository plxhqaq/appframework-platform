using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Persistence.Entities;

/// <summary>
/// 审计日志（横切表，所有模块共用）。写入走 <c>AuditLog</c> 服务，读走管理端 <c>audit.read</c> 权限。
/// </summary>
public sealed class AuditLog : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>应用键（未绑定应用的平台级操作为 <c>null</c>）。</summary>
    public string? AppKey { get; set; }

    /// <summary>操作者 id（用户 id 或 API Key id）。</summary>
    public string? ActorId { get; set; }

    /// <summary>操作者名称（冗余存储，便于审计追溯时不受用户改名影响）。</summary>
    public string? ActorName { get; set; }

    /// <summary>动作标识，例如 <c>versions.release.publish</c>。</summary>
    public required string Action { get; set; }

    /// <summary>目标类型，例如 <c>release</c>。</summary>
    public string? TargetType { get; set; }

    /// <summary>目标 id。</summary>
    public string? TargetId { get; set; }

    /// <summary>结果：<c>success</c> / <c>failure</c>。</summary>
    public required string Result { get; set; }

    /// <summary>失败原因。</summary>
    public string? Message { get; set; }

    /// <summary>客户端 IP。</summary>
    public string? IpAddress { get; set; }

    /// <summary>User-Agent。</summary>
    public string? UserAgent { get; set; }

    /// <summary>链路追踪号。</summary>
    public string? TraceId { get; set; }

    /// <summary>详情 JSON（字段级差异等）。</summary>
    public string? DetailJson { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
