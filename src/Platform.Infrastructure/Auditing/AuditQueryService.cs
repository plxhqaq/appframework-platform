using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Audit;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Auditing;

/// <summary>审计查询（管理端只读）。</summary>
public interface IAuditQueryService
{
    /// <summary>按条件分页查询审计。</summary>
    Task<PageResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}

/// <summary>审计查询实现：只读、按时间倒序，所有过滤条件在数据库侧完成。</summary>
public sealed class AuditQueryService : IAuditQueryService
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public AuditQueryService(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PageResult<AuditLogDto>> QueryAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var paging = query.Paging;

        var source = _db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.AppKey))
        {
            var appKey = query.AppKey.Trim();
            source = source.Where(x => x.AppKey == appKey);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            source = source.Where(x => x.Action.Contains(action));
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim();
            source = source.Where(x =>
                (x.ActorId != null && x.ActorId.Contains(actor))
                || (x.ActorName != null && x.ActorName.Contains(actor)));
        }

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            var result = query.Result.Trim();
            source = source.Where(x => x.Result == result);
        }

        if (query.From is { } from)
        {
            source = source.Where(x => x.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(x => x.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x =>
                x.Action.Contains(keyword)
                || (x.ActorName != null && x.ActorName.Contains(keyword))
                || (x.TargetId != null && x.TargetId.Contains(keyword))
                || (x.Message != null && x.Message.Contains(keyword)));
        }

        var total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.CreatedAt)
            .Skip(paging.Skip)
            .Take(paging.ClampedPageSize)
            .Select(static x => new AuditLogDto(
                x.Id,
                x.AppKey,
                x.ActorId,
                x.ActorName,
                x.Action,
                x.TargetType,
                x.TargetId,
                x.Result,
                x.Message,
                x.IpAddress,
                x.UserAgent,
                x.TraceId,
                x.DetailJson,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

        return PageResult<AuditLogDto>.Create(rows, total, paging);
    }
}
