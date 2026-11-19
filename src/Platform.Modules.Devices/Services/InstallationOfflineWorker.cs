using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>
/// 离线扫描：按安装实例的 <c>HeartbeatSeconds</c> 与全局下限计算阈值，超时即标记离线。
/// </summary>
/// <remarks>
/// 周期比最长阈值短得多，客户端断线后最多一个扫描周期就会在管理端显示离线。
/// 扫描为窄投影 + 一条条件批量更新（见 <see cref="IInstallationPresence.MarkOfflineAsync"/>），
/// 不再把全部在线行物化为实体逐条 SaveChanges；心跳时间另有批量回写周期，
/// 离线阈值已按其窗口钳制，不会误判活跃设备。
/// </remarks>
public sealed class InstallationOfflineWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ScanFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "InstallationOfflineScanFailed"),
        "安装实例离线扫描失败，将在下个周期重试。");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DevicesOptions _options;
    private readonly ILogger<InstallationOfflineWorker> _logger;

    /// <summary>构造。</summary>
    public InstallationOfflineWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DevicesOptions> options,
        ILogger<InstallationOfflineWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.OfflineScanEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _options.ScanIntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var presence = scope.ServiceProvider.GetRequiredService<IInstallationPresence>();
                await presence.MarkOfflineAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ScanFailed(_logger, exception);
            }
        }
    }
}
