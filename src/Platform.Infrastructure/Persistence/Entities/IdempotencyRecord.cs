using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Persistence.Entities;

/// <summary>
/// 幂等记录（横切表）。创建类 POST 携带 <c>Idempotency-Key</c> 时先占位，
/// 成功后写入首次响应，重复请求直接回放。
/// </summary>
/// <remarks>
/// 必须落库而不是放内存：多副本部署时进程内存去重无效（同一键打到不同副本会各自执行）。
/// </remarks>
public sealed class IdempotencyRecord : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>作用域（一般等于模块键 + 端点名），用于隔离不同接口的同名键。</summary>
    public required string Scope { get; set; }

    /// <summary>客户端提供的幂等键。</summary>
    public required string Key { get; set; }

    /// <summary>状态：<c>reserved</c> 表示处理中，<c>completed</c> 表示已完成。</summary>
    public required string Status { get; set; }

    /// <summary>首次响应 JSON（<c>completed</c> 时非空）。</summary>
    public string? ResponseJson { get; set; }

    /// <summary>过期时间；过期后允许同键重新执行。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>处理中。</summary>
    public const string StatusReserved = "reserved";

    /// <summary>已完成。</summary>
    public const string StatusCompleted = "completed";
}
