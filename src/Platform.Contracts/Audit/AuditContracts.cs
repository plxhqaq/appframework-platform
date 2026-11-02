namespace Platform.Contracts.Audit;

/// <summary>审计记录（管理端只读视图）。</summary>
/// <param name="Id">记录 id。</param>
/// <param name="AppKey">应用键；平台级操作为空。</param>
/// <param name="ActorId">操作者 id。</param>
/// <param name="ActorName">操作者名称。</param>
/// <param name="Action">动作标识。</param>
/// <param name="TargetType">目标类型。</param>
/// <param name="TargetId">目标 id。</param>
/// <param name="Result">success / failure。</param>
/// <param name="Message">失败原因或补充说明。</param>
/// <param name="IpAddress">客户端 IP。</param>
/// <param name="UserAgent">User-Agent。</param>
/// <param name="TraceId">链路追踪号。</param>
/// <param name="DetailJson">详情 JSON。</param>
/// <param name="CreatedAt">发生时间。</param>
public sealed record AuditLogDto(
    Guid Id,
    string? AppKey,
    string? ActorId,
    string? ActorName,
    string Action,
    string? TargetType,
    string? TargetId,
    string Result,
    string? Message,
    string? IpAddress,
    string? UserAgent,
    string? TraceId,
    string? DetailJson,
    DateTimeOffset CreatedAt);

/// <summary>审计查询条件。</summary>
public sealed record AuditLogQuery
{
    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; } = 1;

    /// <summary>页大小。</summary>
    public int PageSize { get; init; } = PageQuery.DefaultPageSize;

    /// <summary>关键字（动作/操作者/目标/说明）。</summary>
    public string? Keyword { get; init; }

    /// <summary>应用键（精确匹配）。</summary>
    public string? AppKey { get; init; }

    /// <summary>动作（包含匹配）。</summary>
    public string? Action { get; init; }

    /// <summary>操作者 id 或名称（包含匹配）。</summary>
    public string? Actor { get; init; }

    /// <summary>结果（精确匹配：success / failure）。</summary>
    public string? Result { get; init; }

    /// <summary>起始时间（含）。</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>结束时间（含）。</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>分页视图（服务端钳制）。</summary>
    public PageQuery Paging => new() { Page = Page, PageSize = PageSize, Keyword = Keyword };
}
