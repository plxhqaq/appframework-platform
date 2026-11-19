using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>
/// 心跳批量回写：把内存态心跳时间按 <see cref="DevicesOptions.HeartbeatFlushSeconds"/> 周期
/// 一次性落库（安装实例与节点各一条条件更新），替代心跳逐条写。
/// 关闭 <c>HeartbeatFlushEnabled</c> 时命中路径退回每次心跳逐条落库（集成测试与保守部署用），
/// 开启时纯心跳时间的库内滞后上界为一个回写周期，离线阈值按 <c>HeartbeatFlushSeconds×2</c> 钳制。
/// </summary>
public sealed class HeartbeatFlushWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> FlushFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "HeartbeatFlushFailed"),
        "心跳批量回写失败，将在下个周期重试。");

    private static readonly Action<ILogger, int, Exception?> SweptExpiredLog = LoggerMessage.Define<int>(
        LogLevel.Debug,
        new EventId(2, "HeartbeatCacheSwept"),
        "心跳缓存清理过期条目 {Count} 个。");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DevicesOptions _options;
    private readonly ILogger<HeartbeatFlushWorker> _logger;

    /// <summary>构造。</summary>
    public HeartbeatFlushWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DevicesOptions> options,
        ILogger<HeartbeatFlushWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HeartbeatFlushEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatFlushSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var presence = scope.ServiceProvider.GetRequiredService<IInstallationPresence>();
                await presence.FlushPendingHeartbeatsAsync(stoppingToken);
                var tracker = scope.ServiceProvider.GetRequiredService<InstallationHeartbeatTracker>();
                int swept = tracker.SweepExpired();
                if (swept > 0)
                {
                    SweptExpiredLog(_logger, swept, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                FlushFailed(_logger, exception);
            }
        }
    }
}
