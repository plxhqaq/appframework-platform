using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Services;

namespace Platform.Modules.Versions.Ingestion;

/// <summary>
/// 增量包摄取循环：启动时先回收过期租约，之后按间隔认领并处理待摄取包。
/// 并发度取 <see cref="VersionsOptions.IngestionConcurrency"/>；单轮失败只记日志，下轮自然重试。
/// </summary>
public sealed class ReleasePackageIngestionWorker : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> CycleFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, "PackageIngestionCycleFailed"),
            "增量包摄取循环异常：{Error}");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VersionsOptions _options;
    private readonly ILogger<ReleasePackageIngestionWorker> _logger;

    /// <summary>构造。</summary>
    public ReleasePackageIngestionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<VersionsOptions> options,
        ILogger<ReleasePackageIngestionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IngestionEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.IngestionPollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                CycleFailed(_logger, exception.Message, exception);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
        await ingestion.RecoverStaleAsync(cancellationToken);
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var concurrency = Math.Max(1, _options.IngestionConcurrency);
        var lanes = new Task[concurrency];
        for (var index = 0; index < concurrency; index++)
        {
            lanes[index] = ProcessOneAsync(cancellationToken);
        }

        await Task.WhenAll(lanes);
    }

    private async Task ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
        await ingestion.ProcessNextAsync(cancellationToken);
    }
}
