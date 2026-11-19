using Microsoft.Extensions.Logging;
using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Versions.Services;

/// <summary>
/// 版本域实时提示发布器：把「已发布」「摄取状态迁移」两处事实转成 Hub 提示事件。
/// 推送只是提示，发送失败只记日志，绝不影响已落库的发布/摄取结果；
/// Devices 模块缺席时 <see cref="IInstallationEventRouter"/> 为空，广播静默跳过。
/// </summary>
public interface IVersionEventsPublisher
{
    /// <summary>向设备 Hub 应用组广播 <c>ReleasePublished</c>（客户端自判渠道/分支回拉 check）。</summary>
    Task ReleasePublishedAsync(
        string appKey,
        string channelKey,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>向 AdminHub versions 域广播 <c>ReleaseIngestionProgress</c>。</summary>
    Task IngestionProgressAsync(
        string appKey,
        Guid packageId,
        string status,
        double progress,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class VersionEventsPublisher : IVersionEventsPublisher
{
    private static readonly Action<ILogger, string, Exception?> ReleasePushFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "ReleasePublishedPushFailed"),
            "版本发布提示推送失败（发布结果不受影响）：应用 {AppKey}。");

    private static readonly Action<ILogger, Guid, Exception?> IngestionPushFailedLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2, "IngestionProgressPushFailed"),
            "摄取进度提示推送失败（状态不受影响）：包 {PackageId}。");

    private readonly IInstallationEventRouter? _router;
    private readonly IAdminEventPublisher _adminEvents;
    private readonly IClock _clock;
    private readonly ILogger<VersionEventsPublisher> _logger;

    /// <summary>构造。</summary>
    public VersionEventsPublisher(
        IEnumerable<IInstallationEventRouter> routers,
        IAdminEventPublisher adminEvents,
        IClock clock,
        ILogger<VersionEventsPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(routers);
        _router = routers.FirstOrDefault();
        _adminEvents = adminEvents;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ReleasePublishedAsync(
        string appKey,
        string channelKey,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (_router is null)
        {
            return;
        }

        try
        {
            await _router.ToAppAsync(
                appKey,
                DeviceHubEvents.ReleasePublished,
                new ReleasePublishedNotification(appKey, channelKey, version),
                cancellationToken);
        }
        catch (Exception exception)
        {
            ReleasePushFailedLog(_logger, appKey, exception);
        }
    }

    /// <inheritdoc />
    public async Task IngestionProgressAsync(
        string appKey,
        Guid packageId,
        string status,
        double progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _adminEvents.PublishAsync(
                AdminHubDomains.Versions,
                AdminHubEvents.ReleaseIngestionProgress,
                new AdminReleaseIngestionProgressNotification(
                    appKey, packageId, status, progress, _clock.UtcNow),
                cancellationToken);
        }
        catch (Exception exception)
        {
            IngestionPushFailedLog(_logger, packageId, exception);
        }
    }
}
