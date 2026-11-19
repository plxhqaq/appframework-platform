using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Devices;
using Platform.Contracts.Webhooks;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>配置下发编排：复核兼容矩阵 → 推送提示 → 等待回执。</summary>
public interface IBackupPushService
{
    /// <summary>下发备份到指定安装实例（多选）；应用组广播必须显式声明。</summary>
    Task<PushBackupResult> PushAsync(
        string appKey,
        Guid backupId,
        PushBackupRequest request,
        string? actor,
        CancellationToken cancellationToken = default);
}

/// <summary>下发服务实现。</summary>
/// <remarks>
/// 推送只是提示：客户端收到 <see cref="DeviceHubEvents.ConfigPush"/> 后回拉下载，
/// 应用完成再调用 Hub 的 <c>AcknowledgeConfigPush</c>。服务端只等待回执，不把推送当事实来源。
/// 应用组广播经设备 Hub 的 app 组下发，回执先到者胜（协调器按应用+备份配对）。
/// </remarks>
public sealed class BackupPushService : IBackupPushService
{
    private readonly IBackupService _backups;
    private readonly IPlatformPushCoordinator _coordinator;
    private readonly IInstallationEventRouter? _router;
    private readonly BackupsOptions _options;
    private readonly IPlatformEventPublisher _events;
    private readonly IClock _clock;
    private readonly IInstallationCommandGateway? _installationGateway;

    /// <summary>构造。安装实例网关与事件路由可选：Devices 模块缺席时按安装实例下发与应用组广播均拒绝。</summary>
    public BackupPushService(
        IBackupService backups,
        IPlatformPushCoordinator coordinator,
        IEnumerable<IInstallationEventRouter> routers,
        IOptions<BackupsOptions> options,
        IPlatformEventPublisher events,
        IClock clock,
        IEnumerable<IInstallationCommandGateway> installationGateways)
    {
        _backups = backups;
        _coordinator = coordinator;
        _router = routers.FirstOrDefault();
        _options = options.Value;
        _events = events;
        _clock = clock;
        _installationGateway = installationGateways.FirstOrDefault();
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
        var installationIds = ResolveTargetInstallationIds(request);

        if (installationIds.Count > 0 && request.BroadcastToApp)
        {
            throw BusinessException.Invalid("不能同时指定目标安装实例与应用组广播。");
        }

        if (installationIds.Count == 0 && !request.BroadcastToApp)
        {
            throw BusinessException.Invalid(
                "请至少选择一个目标安装实例；留空不再下发到应用内全部在线连接。");
        }

        if (request.BroadcastToApp && !string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            throw BusinessException.Invalid(
                "应用组广播不支持目标当前配置哈希；快照只能对应单个安装实例。");
        }

        if (installationIds.Count > 1 && !string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            throw BusinessException.Invalid(
                "多安装实例下发不支持目标当前配置哈希；快照只能对应单个安装实例。");
        }

        if (request.BroadcastToApp && (_router?.AppConnectionCount(appKey) ?? 0) == 0)
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

        // 广播等同于下发到应用内所有安装实例，因此与"目标列表含其它安装实例"同等对待。
        if (backup.InstallationId is { } sourceInstallationId
            && !request.ConfirmCrossInstallation
            && (request.BroadcastToApp || installationIds.Any(id => id != sourceInstallationId)))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPushRejected,
                "备份来源安装实例与下发目标不一致（含应用组广播），需要显式确认跨安装实例下发。");
        }

        // 先把所有目标门禁跑完再发送：任何一个不满足就整体拒绝，避免部分安装实例已收到命令。
        var targets = new List<InstallationCommandTarget>(installationIds.Count);
        foreach (var installationId in installationIds)
        {
            targets.Add(await ResolveInstallationTargetAsync(appKey, installationId, cancellationToken));
        }

        Guid? snapshotId = null;
        if (!string.IsNullOrWhiteSpace(request.CurrentHash))
        {
            var snapshot = await _backups.SnapshotAsync(
                appKey,
                backupId,
                installationIds.Count > 0 ? installationIds[0] : null,
                request.CurrentHash,
                actor,
                cancellationToken);
            snapshotId = snapshot.Id;
        }

        if (targets.Count == 0)
        {
            return await PushToAppGroupAsync(appKey, backup, request, snapshotId, cancellationToken);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PushAckTimeoutSeconds));
        var results = await Task.WhenAll(
            targets.Select(target => PushToInstallationAsync(
                appKey, backup, request, target, timeout, cancellationToken)));

        if (results.All(static result => !result.Delivered))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                "目标安装实例连接已断开，无法下发配置。");
        }

        if (results.All(static result => !result.Acked))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsAckTimeout,
                $"目标安装实例在 {timeout.TotalSeconds:0} 秒内没有回执。");
        }

        var successCount = results.Count(static result => result.Acked && result.Success);
        var success = successCount == results.Length;
        var message = results.Length == 1
            ? results[0].Message
            : success
                ? $"全部 {results.Length} 个安装实例已回执并应用。"
                : $"{successCount}/{results.Length} 个安装实例成功，{results.Length - successCount} 个失败。";

        if (success)
        {
            await PublishPushedAsync(
                appKey,
                backup,
                installationIds,
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

    private static List<Guid> ResolveTargetInstallationIds(PushBackupRequest request)
    {
        var ids = new List<Guid>();
        if (request.TargetInstallationId is { } single)
        {
            if (single == Guid.Empty)
            {
                throw BusinessException.Invalid("目标安装实例 id 不合法。");
            }

            ids.Add(single);
        }

        foreach (var id in request.TargetInstallationIds ?? [])
        {
            if (id == Guid.Empty)
            {
                throw BusinessException.Invalid("目标安装实例 id 不合法。");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>下发前的目标门禁：不存在/停用/离线/运行中分别对应既有错误码。</summary>
    private async Task<InstallationCommandTarget> ResolveInstallationTargetAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var gateway = _installationGateway
                      ?? throw new BusinessException(
                          ApiErrorCode.BackupsPushRejected,
                          "设备域未启用，无法按安装实例下发配置。");

        var target = await gateway.GetTargetAsync(appKey, installationId, cancellationToken)
                     ?? throw BusinessException.NotFound(
                         ApiErrorCode.DevicesInstallationNotFound,
                         $"安装实例 {installationId} 不存在。");
        if (!target.NodeEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesNodeDisabled,
                $"安装实例 {installationId} 所在节点已被停用，无法下发配置。");
        }

        if (!target.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesInstallationDisabled,
                $"安装实例 {installationId} 已被停用，无法下发配置。");
        }

        if (!string.Equals(target.OnlineStatus, DeviceOnlineStatuses.Online, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesOffline,
                $"安装实例 {installationId} 离线，无法下发配置。");
        }

        if (!string.Equals(target.StationStatus, DeviceStationStatuses.Idle, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.DevicesBusy,
                $"安装实例 {installationId} 正在运行，请切换到待机后再下发。");
        }

        return target;
    }

    /// <summary>应用组广播：显式声明时才走这里，先注册等待再发送；回执先到者胜。</summary>
    private async Task<PushBackupResult> PushToAppGroupAsync(
        string appKey,
        BackupDto backup,
        PushBackupRequest request,
        Guid? snapshotId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PushAckTimeoutSeconds));
        var waitTask = _coordinator.WaitForAckAsync(appKey, backup.Id, timeout, cancellationToken);

        var command = new DeviceConfigPush(
            backup.Id,
            backup.Kind,
            PayloadJson: null,
            DownloadUrl: $"/api/v1/client/backups/{backup.Id:D}/download",
            Sha256: backup.Sha256,
            RestartAfterApply: request.RestartAfterApply,
            Branch: backup.Channel);

        // 前置门禁已保证在线连接数 > 0，即路由必然存在。
        await _router!.ToAppAsync(appKey, DeviceHubEvents.ConfigPush, command, cancellationToken);

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
                targetInstallationIds: [],
                snapshotId,
                successCount: 1,
                failureCount: 0,
                cancellationToken);
        }

        return new PushBackupResult(true, ack.Succeeded, ack.Message, snapshotId);
    }

    /// <summary>按安装实例下发单个：门禁已在发送前完成，这里只发通用命令并收集回执。</summary>
    private async Task<PushTargetResult> PushToInstallationAsync(
        string appKey,
        BackupDto backup,
        PushBackupRequest request,
        InstallationCommandTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var gateway = _installationGateway!;
        var command = new DeviceConfigPush(
            backup.Id,
            backup.Kind,
            PayloadJson: null,
            DownloadUrl: $"/api/v1/client/backups/{backup.Id:D}/download",
            Sha256: backup.Sha256,
            RestartAfterApply: request.RestartAfterApply,
            Branch: backup.Channel);

        var result = await gateway.PushConfigAsync(
            appKey, target.InstallationId, command, timeout, cancellationToken);

        return new PushTargetResult(
            target.InstallationId,
            target.NodeId,
            result.Delivered,
            result.AckReceived,
            result.Succeeded,
            result.Message);
    }

    private Task PublishPushedAsync(
        string appKey,
        BackupDto backup,
        IReadOnlyList<Guid> targetInstallationIds,
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
                    targetInstallationIds,
                    snapshotId,
                    successCount,
                    failureCount,
                },
                _clock.UtcNow),
            cancellationToken);
    }
}
