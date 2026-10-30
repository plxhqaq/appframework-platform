namespace Platform.Contracts;

/// <summary>
/// 分页查询。服务端一律对 <see cref="Page"/> / <see cref="PageSize"/> 做钳制，
/// 不接受客户端传入的超大页（避免一次拉全表）。
/// </summary>
public sealed record PageQuery
{
    /// <summary>默认页大小。</summary>
    public const int DefaultPageSize = 20;

    /// <summary>允许的最大页大小。</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; } = 1;

    /// <summary>页大小。</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>关键字（各模块自行解释：名称/版本号/机器码…）。</summary>
    public string? Keyword { get; init; }

    /// <summary>排序表达式（<c>field</c> 或 <c>-field</c>），实现由模块决定。</summary>
    public string? Sort { get; init; }

    /// <summary>游标（大表翻页用；与 <see cref="Page"/> 二选一）。</summary>
    public string? Cursor { get; init; }

    /// <summary>钳制后的跳过条数。</summary>
    public int Skip => (ClampedPage - 1) * ClampedPageSize;

    /// <summary>钳制后的页码（≥1）。</summary>
    public int ClampedPage => Page < 1 ? 1 : Page;

    /// <summary>钳制后的页大小（1..<see cref="MaxPageSize"/>）。</summary>
    public int ClampedPageSize => PageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => PageSize,
    };
}

/// <summary>分页结果。</summary>
/// <typeparam name="T">条目类型。</typeparam>
public sealed record PageResult<T>
{
    /// <summary>当前页条目。</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>当前页（钳制后）。</summary>
    public int Page { get; init; } = 1;

    /// <summary>当前页大小（钳制后）。</summary>
    public int PageSize { get; init; } = PageQuery.DefaultPageSize;

    /// <summary>符合条件的总条数。</summary>
    public long Total { get; init; }

    /// <summary>下一页游标；<c>null</c> 表示没有下一页。</summary>
    public string? NextCursor { get; init; }

    /// <summary>总页数（按 <see cref="PageSize"/> 计算；<see cref="PageSize"/> 为 0 时为 0）。</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)((Total + PageSize - 1) / PageSize);

#pragma warning disable CA1000 // 泛型类上的静态工厂：调用点 PageResult<T>.Empty(...) 比新增 PageResults 类更直观

    /// <summary>空页。</summary>
    public static PageResult<T> Empty(PageQuery? query = null) => new()
    {
        Items = [],
        Page = query?.ClampedPage ?? 1,
        PageSize = query?.ClampedPageSize ?? PageQuery.DefaultPageSize,
        Total = 0,
    };

    /// <summary>按查询条件与总数构造。</summary>
    public static PageResult<T> Create(IReadOnlyList<T> items, long total, PageQuery query, string? nextCursor = null) => new()
    {
        Items = items,
        Page = query.ClampedPage,
        PageSize = query.ClampedPageSize,
        Total = total,
        NextCursor = nextCursor,
    };

#pragma warning restore CA1000
}
