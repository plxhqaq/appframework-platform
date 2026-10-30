using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Kernel.Paging;

namespace Platform.Infrastructure.Persistence;

/// <summary>EF Core 侧分页扩展（与 Kernel 的内存版分页保持同一返回结构）。</summary>
public static class EfPagingExtensions
{
    /// <summary>先取总数再取当前页；两条 SQL 都在同一上下文内完成。</summary>
    public static async Task<PageResult<T>> ToPageResultAsync<T>(
        this IQueryable<T> source,
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            return PageResult<T>.Empty(query);
        }

        var items = await source
            .ApplyPaging(query)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PageResult<T>.Create(items, total, query);
    }
}
