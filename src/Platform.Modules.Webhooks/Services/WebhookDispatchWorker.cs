using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Webhooks.Options;

namespace Platform.Modules.Webhooks.Services;

/// <summary>
/// 后台投递循环：按配置间隔扫描到期投递。
/// </summary>
/// <remarks>
/// 单实例假设：启动期 <c>SingleInstanceGuard</c> 用 PostgreSQL advisory lock 挡住第二个副本，
/// 因此这里不需要分布式租约；扩容前需改为行级抢占。接收方仍应按
/// <c>X-Platform-Delivery</c> 幂等，抵御重试与人工重投。
/// </remarks>
public sealed class WebhookDispatchWorker : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> StartFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, "WebhookDispatchFailed"),
            "Webhook 投递循环异常：{Error}");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WebhooksOptions _options;
    private readonly ILogger<WebhookDispatchWorker> _logger;

    /// <summary>构造。</summary>
    public WebhookDispatchWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<WebhooksOptions> options,
        ILogger<WebhookDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DispatcherEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.DispatchIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();
                await dispatcher.DispatchDueAsync(Math.Max(1, _options.BatchSize), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 一轮失败不终止循环：数据库短暂不可用后应自行恢复。
                StartFailed(_logger, exception.Message, exception);
            }
        }
    }
}
