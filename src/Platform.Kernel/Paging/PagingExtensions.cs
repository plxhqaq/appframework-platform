using Platform.Contracts;

namespace Platform.Kernel.Paging;

/// <summary>分页扩展（不依赖 EF Core，保证 Kernel 无数据访问负担）。</summary>
public static class PagingExtensions
{
    /// <summary>对可查询序列套用钳制后的分页。</summary>
    public static IQueryable<T> ApplyPaging<T>(this IQueryable<T> source, PageQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        return source.Skip(query.Skip).Take(query.ClampedPageSize);
    }

    /// <summary>把内存集合包装成分页结果。</summary>
    public static PageResult<T> ToPageResult<T>(this IReadOnlyList<T> items, long total, PageQuery query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        return PageResult<T>.Create(items, total, query);
    }
}
