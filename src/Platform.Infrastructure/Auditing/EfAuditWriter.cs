using Microsoft.Extensions.Logging;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.Auditing;

/// <summary>基于 PostgreSQL 的审计写入。</summary>
/// <remarks>
/// 审计失败只记日志、不向调用方抛出：审计是旁路能力，不能因为审计表短暂不可用
/// 把登录/发布这类主业务一起拖死。代价是极端情况下可能丢一条审计，因此日志里保留完整条目。
/// </remarks>
public sealed class EfAuditWriter : IAuditWriter
{
    private static readonly Action<ILogger, string, string?, string?, string?, Exception?>
        AuditWriteFailed = LoggerMessage.Define<string, string?, string?, string?>(
            LogLevel.Error,
            new EventId(1, "AuditWriteFailed"),
            "审计写入失败：action={Action} target={TargetType}/{TargetId} actor={ActorId}");

    private readonly PlatformDbContext _db;
    private readonly ILogger<EfAuditWriter> _logger;

    /// <summary>构造。</summary>
    public EfAuditWriter(PlatformDbContext db, ILogger<EfAuditWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entity = new AuditLog
        {
            AppKey = entry.AppKey,
            ActorId = entry.ActorId,
            ActorName = entry.ActorName,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Result = entry.Result,
            Message = entry.Message,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            TraceId = entry.TraceId,
            DetailJson = entry.DetailJson,
        };

        try
        {
            _db.AuditLogs.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AuditWriteFailed(
                _logger,
                entry.Action,
                entry.TargetType,
                entry.TargetId,
                entry.ActorId,
                exception);
        }
    }
}
