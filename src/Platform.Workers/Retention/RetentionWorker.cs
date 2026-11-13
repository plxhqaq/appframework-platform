using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;

namespace Platform.Workers.Retention;

/// <summary>
/// 模块保留策略清理调度：周期性地执行各模块注册的 <see cref="IRetentionCleanupTask"/>。
/// 单个任务失败只记录日志，不阻断其他模块，下个周期自然重试。
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> TaskFailed = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(1, "RetentionTaskFailed"),
        "保留策略任务 {Task} 执行失败，将在下个周期重试。");

    private static readonly Action<ILogger, string, int, int, Exception?> TaskCompleted =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(2, "RetentionTaskCompleted"),
            "保留策略任务 {Task} 完成：删除行 {Rows}，删除文件 {Files}。");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly ILogger<RetentionWorker> _logger;

    /// <summary>构造。</summary>
    public RetentionWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> options,
        ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var interval = TimeSpan.FromSeconds(Math.Max(5, _options.CurrentValue.IntervalSeconds));
                await Task.Delay(interval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                TaskFailed(_logger, "scheduler", exception);
            }
        }
    }

    private static void Report(PlatformMetrics metrics, string task, RetentionCleanupResult result)
    {
        if (result.RowsDeleted > 0)
        {
            metrics.Increment(
                PlatformMetricNames.RetentionDeletedRows,
                PlatformMetricNames.RetentionDeletedRowsHelp,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = task },
                result.RowsDeleted);
        }

        if (result.FilesDeleted > 0)
        {
            metrics.Increment(
                PlatformMetricNames.RetentionDeletedFiles,
                PlatformMetricNames.RetentionDeletedFilesHelp,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = task },
                result.FilesDeleted);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var metrics = scope.ServiceProvider.GetRequiredService<PlatformMetrics>();
        foreach (var task in scope.ServiceProvider.GetServices<IRetentionCleanupTask>())
        {
            try
            {
                var result = await task.RunAsync(cancellationToken);
                if (result.RowsDeleted > 0 || result.FilesDeleted > 0)
                {
                    TaskCompleted(_logger, task.Name, result.RowsDeleted, result.FilesDeleted, null);
                }

                Report(metrics, task.Name, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                TaskFailed(_logger, task.Name, exception);
            }
        }
    }
}
