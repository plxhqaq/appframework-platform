using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Devices;
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
    /// <summary>下发备份到指定设备（多选）；应用组广播必须显式声明。</summary>
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
    private readonly IDeviceCommandGateway? _deviceGateway;

    /// <summary>构造。设备网关可选：Devices 模块缺席时应用组下发仍然可用。</summary>
    public BackupPushService(
        IBackupService backups,
        IPlatformPushCoordinator coordinator,
        IHubContext<PlatformHub> hub,
        IOptions<BackupsOptions> options,
        IPlatformEventPublisher events,
        IClock clock,
        IEnumerable<IDeviceCommandGateway> deviceGateways)
    {
        _backups = backups;
        _coordinator = coordinator;
        _hub = hub;
        _options = options.Value;
        _events = events;
        _clock = clock;
        _deviceGateway = deviceGateways.FirstOrDefault();
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
        var deviceIds = ResolveTargetDeviceIds(request);

        if (deviceIds.Count > 0 && request.BroadcastToApp)
        {
            throw BusinessException.Invalid("不能同时指定目标设备与应用组广播。");
        }

        if (deviceIds.Count == 0 && !request.BroadcastToApp)
        {
            throw BusinessException.Invalid(
                "请至少选择一台目标设备；留空不再下发到应用内全部在线连接。");
        }

        if (deviceIds.Count > 1 && !string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            throw BusinessException.Invalid(
                "多设备下发不支持目标当前配置哈希；快照只能对应单台设备。");
        }

        if (request.BroadcastToApp && _coordinator.ConnectionCount(appKey) == 0)
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

        if (backup.DeviceId is { } sourceDeviceId
            && deviceIds.Any(id => id != sourceDeviceId)
            && !request.ConfirmCrossDevice)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPushRejected,
                "备份来源设备与目标设备不一致，需要显式确认跨设备下发。");
        }

        // 先把所有目标门禁跑完再发送：任何一台不满足就整体拒绝，避免部分设备已收到命令。
        var targets = new List<DeviceCommandTarget>(deviceIds.Count);
        foreach (var deviceId in deviceIds)
        {
            targets.Add(await ResolveDeviceTargetAsync(appKey, deviceId, cancellationToken));
        }

        Guid? snapshotId = null;
        if (!string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            var snapshot = await _backups.SnapshotAsync(
                appKey,
                backupId,
                deviceIds.Count > 0 ? deviceIds[0] : null,
                request.CurrentHash,
                actor,
                cancellationToken);
            snapshotId = snapshot.Id;
        }

        if (targets.Count == 0)
        {
            return await PushToAppGroupAsync(appKey, backup, snapshotId, cancellationToken);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PushAckTimeoutSeconds));
        var results = await Task.WhenAll(
            targets.Select(target => PushToDeviceAsync(
                appKey, backup, request, target.DeviceId, timeout, cancellationToken)));

        if (results.All(static result => !result.Delivered))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                "目标设备连接已断开，无法下发配置。");
        }

        if (results.All(static result => !result.Acked))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsAckTimeout,
                $"目标设备在 {timeout.TotalSeconds:0} 秒内没有回执。");
        }

        var successCount = results.Count(static result => result.Acked && result.Success);
        var success = successCount == results.Length;
        var message = results.Length == 1
            ? results[0].Message
            : success
                ? $"全部 {results.Length} 台设备已回执并应用。"
                : $"{successCount}/{results.Length} 台设备成功，{results.Length - successCount} 台失败。";

        if (success)
        {
            await PublishPushedAsync(
                appKey,
                backup,
                deviceIds,
                snapshotId,
                successCount: results.Length,
                failureCount: 0,
                cancellationToken);
        }

        return new PushBackupResult(
            results.All(static result => result.Acked),
            success,
            message,
            snapshotId,
            results);
    }

    private static List<Guid> ResolveTargetDeviceIds(PushBackupRequest request)
    {
        var ids = new List<Guid>();
        if (request.TargetDeviceId is { } single)
        {
            if (single == Guid.Empty)
            {
                throw BusinessException.Invalid("目标设备 id 不合法。");
            }

            ids.Add(single);
        }

        foreach (var id in request.TargetDeviceIds ?? [])
        {
            if (id == Guid.Empty)
            {
                throw BusinessException.Invalid("目标设备 id 不合法。");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>下发前的目标门禁：不存在/停用/离线/运行中分别对应既有设备错误码。</summary>
    private async Task<DeviceCommandTarget> ResolveDeviceTargetAsync(
        string appKey,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var gateway = _deviceGateway
                      ?? throw new BusinessException(
                          ApiErrorCode.BackupsPushRejected,
                          "设备域未启用，无法按设备下发配置。");

        var target = await gateway.GetTargetAsync(appKey, deviceId, cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.DevicesNotFound,
                         $"设备 {deviceId} 不存在。");
        if (!target.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesDisabled,
                $"设备 {deviceId} 已被停用，无法下发配置。");
        }

        if (!string.Equals(target.OnlineStatus, DeviceOnlineStatuses.Online, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                $"设备 {deviceId} 离线，无法下发配置。");
        }

        if (!string.Equals(target.StationStatus, DeviceStationStatuses.Idle, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesBusy,
                $"设备 {deviceId} 正在运行，请切换到待机后再下发。");
        }

        return target;
    }

    /// <summary>应用组广播：显式声明时才走这里，先注册等待再发送。</summary>
    private async Task<PushBackupResult> PushToAppGroupAsync(
        string appKey,
        BackupDto backup,
        Guid? snapshotId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PushAckTimeoutSeconds));
        var waitTask = _coordinator.WaitForAckAsync(appKey, backup.Id, timeout, cancellationToken);

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
            await PublishPushedAsync(
                appKey,
                backup,
                targetDeviceIds: [],
                snapshotId,
                successCount: 1,
                failureCount: 0,
                cancellationToken);
        }

        return new PushBackupResult(true, ack.Succeeded, ack.Message, snapshotId);
    }

    /// <summary>按设备下发单台：门禁已在发送前完成，这里只发通用命令并收集回执。</summary>
    private async Task<PushTargetResult> PushToDeviceAsync(
        string appKey,
        BackupDto backup,
        PushBackupRequest request,
        Guid deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var gateway = _deviceGateway!;
        var command = new DeviceConfigPush(
            backup.Id,
            backup.Kind,
            PayloadJson: null,
            DownloadUrl: $"/api/v1/client/backups/{backup.Id:D}/download",
            Sha256: backup.Sha256,
            RestartAfterApply: request.RestartAfterApply,
            Branch: backup.Channel);

        var result = await gateway.PushConfigAsync(
            appKey, deviceId, command, timeout, cancellationToken);

        return new PushTargetResult(
            deviceId,
            result.Delivered,
            result.AckReceived,
            result.Succeeded,
            result.Message);
    }

    private Task PublishPushedAsync(
        string appKey,
        BackupDto backup,
        IReadOnlyList<Guid> targetDeviceIds,
        Guid? snapshotId,
        int successCount,
        int failureCount,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(
            new PlatformEvent(
                WebhookConventions.EventBackupPushed,
                appKey,
                new
                {
                    backupId = backup.Id,
                    namespaceKey = backup.NamespaceKey,
                    targetDeviceIds,
                    snapshotId,
                    successCount,
                    failureCount,
                },
                _clock.UtcNow),
            cancellationToken);
    }
}
