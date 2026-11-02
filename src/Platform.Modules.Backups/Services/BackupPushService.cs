using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Hubs;
using Platform.Contracts.Webhooks;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Hubs;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>配置下发编排：复核兼容矩阵 → 推送提示 → 等待回执。</summary>
public interface IBackupPushService
{
    /// <summary>下发备份到应用内在线连接（或指定设备）。</summary>
    Task<PushBackupResult> PushAsync(
        string appKey,
        Guid backupId,
        PushBackupRequest request,
        string? actor,
        CancellationToken cancellationToken = default);
}

/// <summary>下发服务实现。</summary>
/// <remarks>
/// 推送只是提示：客户端收到 <see cref="PlatformHubEvents.ConfigPush"/> 后回拉下载，
/// 应用完成再调用 Hub 的 <c>ConfigPushAck</c>。服务端只等待回执，不把推送当事实来源。
/// </remarks>
public sealed class BackupPushService : IBackupPushService
{
    private readonly IBackupService _backups;
    private readonly IPlatformPushCoordinator _coordinator;
    private readonly IHubContext<PlatformHub> _hub;
    private readonly BackupsOptions _options;
    private readonly IPlatformEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public BackupPushService(
        IBackupService backups,
        IPlatformPushCoordinator coordinator,
        IHubContext<PlatformHub> hub,
        IOptions<BackupsOptions> options,
        IPlatformEventPublisher events,
        IClock clock)
    {
        _backups = backups;
        _coordinator = coordinator;
        _hub = hub;
        _options = options.Value;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PushBackupResult> PushAsync(
        string appKey,
        Guid backupId,
        PushBackupRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var backup = await _backups.GetAsync(appKey, backupId, cancellationToken);

        if (_coordinator.ConnectionCount(appKey) == 0)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPushRejected,
                "目标应用当前没有在线连接，无法下发。");
        }

        var compatibility = BackupCompatibilityRules.Evaluate(
            backup.Channel,
            backup.FrameworkVersion,
            request.TargetChannel,
            request.TargetFrameworkVersion,
            request.Force);
        if (!compatibility.Compatible)
        {
            var code = compatibility.Reason?.StartsWith("渠道不同", StringComparison.Ordinal) == true
                ? ApiErrorCode.BackupsChannelMismatch
                : ApiErrorCode.BackupsSchemaIncompatible;
            throw new BusinessException(code, compatibility.Reason);
        }

        if (request.TargetDeviceId is { } targetDeviceId
            && backup.DeviceId is { } sourceDeviceId
            && targetDeviceId != sourceDeviceId
            && !request.ConfirmCrossDevice)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPushRejected,
                "备份来源设备与目标设备不一致，需要显式确认跨设备下发。");
        }

        Guid? snapshotId = null;
        if (!string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            var snapshot = await _backups.SnapshotAsync(
                appKey,
                backupId,
                request.TargetDeviceId,
                request.CurrentHash,
                actor,
                cancellationToken);
            snapshotId = snapshot.Id;
        }

        // 先注册等待再发送：回执可能比 SendAsync 返回还快。
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PushAckTimeoutSeconds));
        var waitTask = _coordinator.WaitForAckAsync(appKey, backupId, timeout, cancellationToken);

        await _hub.Clients
            .Group(PlatformHubGroups.App(appKey))
            .SendAsync(
                PlatformHubEvents.ConfigPush,
                new ConfigPushNotification(
                    appKey,
                    backup.Id,
                    backup.NamespaceKey,
                    backup.SchemaVersion,
                    Required: true),
                cancellationToken);

        var ack = await waitTask;
        if (!ack.Received)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsAckTimeout,
                $"目标在 {timeout.TotalSeconds:0} 秒内没有回执。");
        }

        if (ack.Succeeded)
        {
            await _events.PublishAsync(
                new PlatformEvent(
                    WebhookConventions.EventBackupPushed,
                    appKey,
                    new
                    {
                        backupId = backup.Id,
                        namespaceKey = backup.NamespaceKey,
                        targetDeviceId = request.TargetDeviceId,
                        snapshotId,
                    },
                    _clock.UtcNow),
                cancellationToken);
        }

        return new PushBackupResult(true, ack.Succeeded, ack.Message, snapshotId);
    }
}
