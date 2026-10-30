using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;

namespace Platform.Workers.Maintenance;

/// <summary>过期数据清理。所有删除都按批次进行，避免大事务锁表。</summary>
public sealed class MaintenanceWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> RunFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "MaintenanceRunFailed"),
        "后台维护任务执行失败，将在下个周期重试。");

    private static readonly Action<ILogger, int, int, Exception?> CleanupCompleted =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(2, "MaintenanceCleanupCompleted"),
            "维护清理完成：幂等记录 {Idempotency} 条，审计日志 {Audit} 条。");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<MaintenanceOptions> _options;
    private readonly ILogger<MaintenanceWorker> _logger;

    /// <summary>构造。</summary>
    public MaintenanceWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<MaintenanceOptions> options,
        ILogger<MaintenanceWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一个间隔，避免和迁移/预热抢数据库连接。
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _options.CurrentValue.IntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                RunFailed(_logger, exception);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var idempotencyCutoff = now.AddHours(-Math.Max(1, options.IdempotencyRetentionHours));
        var auditCutoff = now.AddDays(-Math.Max(1, options.AuditRetentionDays));
        var batchSize = Math.Max(100, options.BatchSize);

        var deletedIdempotency = await db.IdempotencyRecords
            .Where(r => r.ExpiresAt < idempotencyCutoff)
            .OrderBy(static r => r.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);

        var deletedAudit = await db.AuditLogs
            .Where(r => r.CreatedAt < auditCutoff)
            .OrderBy(static r => r.CreatedAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);

        if (deletedIdempotency > 0 || deletedAudit > 0)
        {
            CleanupCompleted(_logger, deletedIdempotency, deletedAudit, null);
        }
    }
}
