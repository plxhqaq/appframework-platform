namespace Platform.Kernel.Abstractions;

/// <summary>审计条目。业务只描述“发生了什么”，落库细节由基础设施负责。</summary>
public sealed record AuditEntry
{
    /// <summary>应用键；平台级操作为空。</summary>
    public string? AppKey { get; init; }

    /// <summary>操作者 id（用户或 API Key）。</summary>
    public string? ActorId { get; init; }

    /// <summary>操作者名称（冗余存储，不受改名影响）。</summary>
    public string? ActorName { get; init; }

    /// <summary>动作标识，例如 <c>apps.apikey.create</c>。</summary>
    public required string Action { get; init; }

    /// <summary>目标类型，例如 <c>app_api_key</c>。</summary>
    public string? TargetType { get; init; }

    /// <summary>目标 id。</summary>
    public string? TargetId { get; init; }

    /// <summary>结果：<c>success</c> / <c>failure</c>。</summary>
    public required string Result { get; init; }

    /// <summary>失败原因或补充说明。</summary>
    public string? Message { get; init; }

    /// <summary>客户端 IP。</summary>
    public string? IpAddress { get; init; }

    /// <summary>User-Agent。</summary>
    public string? UserAgent { get; init; }

    /// <summary>链路追踪号。</summary>
    public string? TraceId { get; init; }

    /// <summary>详情 JSON（字段级差异等）。</summary>
    public string? DetailJson { get; init; }
}

/// <summary>审计写入。失败不得阻断主业务，但必须留下服务端日志。</summary>
public interface IAuditWriter
{
    /// <summary>写入一条审计。</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary><see cref="AuditEntry.Result"/> 取值。</summary>
public static class AuditResults
{
    public const string Success = "success";
    public const string Failure = "failure";
}
