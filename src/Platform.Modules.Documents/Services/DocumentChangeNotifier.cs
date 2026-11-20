using Microsoft.Extensions.Logging;
using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Documents.Services;

/// <summary>客户端可见文档变化提示：app 组广播，客户端按需回拉 manifest；推送失败不影响落库结果。</summary>
public sealed class DocumentChangeNotifier
{
    private static readonly Action<ILogger, string, Exception?> PushFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "DocumentChangedPushFailed"),
            "文档变更提示推送失败（落库结果不受影响）：应用 {AppKey}。");

    private readonly IInstallationEventRouter? _router;
    private readonly ILogger<DocumentChangeNotifier> _logger;

    /// <summary>构造。</summary>
    public DocumentChangeNotifier(
        IEnumerable<IInstallationEventRouter> routers,
        ILogger<DocumentChangeNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(routers);
        _router = routers.FirstOrDefault();
        _logger = logger;
    }

    /// <summary>发布 <see cref="DeviceHubEvents.DocumentChanged"/>；无路由器时静默跳过。</summary>
    public async Task NotifyClientChangedAsync(
        string appKey,
        string collectionKey,
        CancellationToken cancellationToken)
    {
        if (_router is null)
        {
            return;
        }

        try
        {
            await _router.ToAppAsync(
                appKey,
                DeviceHubEvents.DocumentChanged,
                new DocumentChangedNotification(appKey, collectionKey),
                cancellationToken);
        }
        catch (Exception exception)
        {
            PushFailedLog(_logger, appKey, exception);
        }
    }
}
