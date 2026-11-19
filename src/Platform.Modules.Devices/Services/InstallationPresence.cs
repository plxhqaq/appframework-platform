using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>安装实例在线状态维护：心跳、工位、报警与离线扫描。</summary>
public interface IInstallationPresence
{
    /// <summary>刷新心跳与报警；返回最新概要（供状态提示推送）。</summary>
    Task<InstallationSummaryDto> HeartbeatAsync(
        string appKey,
        Guid installationId,
        DeviceHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新工位状态（只接受 <c>idle</c>/<c>online</c>）。</summary>
    Task<InstallationSummaryDto> ReportStationStateAsync(
        string appKey,
        Guid installationId,
        DeviceStationStateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>把心跳超时的在线安装实例标记为离线；返回本次标记数量。</summary>
    Task<int> MarkOfflineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 连接断开且该安装实例已无其它活跃连接时立即标记离线，
    /// 不等待心跳超时扫描（客户端正常退出的场景）。
    /// </summary>
    /// <param name="appKey">应用键。</param>
    /// <param name="installationId">安装实例主键。</param>
    /// <param name="disconnectedAt">判定断开的时刻；此后登记的新连接不得被离线覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task MarkDisconnectedAsync(
        string appKey,
        Guid installationId,
        DateTimeOffset disconnectedAt,
        CancellationToken cancellationToken = default);

    /// <summary>把内存态心跳时间批量回库；返回回写条目数（由 <see cref="HeartbeatFlushWorker"/> 周期调用）。</summary>
    Task<int> FlushPendingHeartbeatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 安装实例在线状态实现。心跳默认走内存态（<see cref="InstallationHeartbeatTracker"/>）：
/// 命中缓存零读写库，状态三值跃迁才单行写穿并即时发事件，心跳时间由回写循环批量落库；
/// 未命中（首拍、TTL 过期、注册/停用失效后）回源查库，同时兜住启用位校验。
/// </summary>
public sealed class InstallationPresence : IInstallationPresence
{
    private static readonly Action<ILogger, int, Exception?> OfflineMarkedLog = LoggerMessage.Define<int>(
        LogLevel.Information,
        new EventId(1, "InstallationsMarkedOffline"),
        "离线扫描完成：{Count} 个安装实例标记为离线。");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly DevicesOptions _options;
    private readonly IInstallationStateNotifier _states;
    private readonly InstallationHeartbeatTracker _heartbeats;
    private readonly ILogger<InstallationPresence> _logger;

    /// <summary>构造。</summary>
    public InstallationPresence(
        PlatformDbContext db,
        IClock clock,
        IOptions<DevicesOptions> options,
        IInstallationStateNotifier states,
        InstallationHeartbeatTracker heartbeats,
        ILogger<InstallationPresence> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _states = states;
        _heartbeats = heartbeats;
        _logger = logger;
    }

    private static InstallationStateSnapshot SnapshotOf(DeviceInstallation installation) =>
        new(installation.OnlineStatus, installation.StationStatus, installation.AlarmLevel);

    private static InstallationStateSnapshot SnapshotOf(InstallationSummaryDto summary) =>
        new(summary.OnlineStatus, summary.StationStatus, summary.AlarmLevel);

    /// <inheritdoc />
    public async Task<InstallationSummaryDto> HeartbeatAsync(
        string appKey,
        Guid installationId,
        DeviceHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DeviceHeartbeatStatuses.IsKnown(request.Status))
        {
            throw BusinessException.Invalid(
                $"未知心跳状态 '{request.Status}'，允许值：{DeviceHeartbeatStatuses.Offline}/{DeviceHeartbeatStatuses.Online}/{DeviceHeartbeatStatuses.Idle}。");
        }

        if (!DeviceAlarmLevels.IsKnown(request.AlarmLevel))
        {
            throw BusinessException.Invalid(
                $"未知报警级别 '{request.AlarmLevel}'，允许值：{DeviceAlarmLevels.None}/{DeviceAlarmLevels.Info}/{DeviceAlarmLevels.Warning}/{DeviceAlarmLevels.Error}。");
        }

        var alarmMessage = DeviceValidation.Optional(
            request.AlarmMessage, DeviceConventions.MaxAlarmMessageLength, "报警摘要");
        if (string.Equals(request.AlarmLevel, DeviceAlarmLevels.None, StringComparison.Ordinal))
        {
            alarmMessage = null;
        }

        var now = _clock.UtcNow;
        if (_heartbeats.TryGet(appKey, installationId, out var cached))
        {
            InstallationSummaryDto current = cached.Summary;
            string online = current.OnlineStatus;
            string station = current.StationStatus;
            ApplyHeartbeatStatus(request.Status, ref online, ref station);
            bool flipped = !string.Equals(online, current.OnlineStatus, StringComparison.Ordinal)
                           || !string.Equals(station, current.StationStatus, StringComparison.Ordinal)
                           || !string.Equals(request.AlarmLevel, current.AlarmLevel, StringComparison.Ordinal);
            bool messageChanged = !string.Equals(
                alarmMessage ?? string.Empty, current.AlarmMessage ?? string.Empty, StringComparison.Ordinal);

            var next = current with
            {
                OnlineStatus = online,
                StationStatus = station,
                AlarmLevel = request.AlarmLevel,
                AlarmMessage = alarmMessage,
                LastHeartbeatAt = now,
            };

            bool persisted = false;
            if (flipped || messageChanged || !_options.HeartbeatFlushEnabled)
            {
                await PersistStateAsync(
                    installationId, cached.NodeId, online, station,
                    request.AlarmLevel, alarmMessage, now, cancellationToken);
                persisted = true;
            }

            _heartbeats.Touch(installationId, next, now, persisted);
            if (flipped)
            {
                await _states.NotifyAsync(
                    appKey, installationId, cached.NodeId,
                    SnapshotOf(current), SnapshotOf(next), cancellationToken);
            }

            return next;
        }

        var installation = await FindAsync(appKey, installationId, cancellationToken);
        InstallationStateSnapshot previous = SnapshotOf(installation);

        string loadedOnline = installation.OnlineStatus;
        string loadedStation = installation.StationStatus;
        ApplyHeartbeatStatus(request.Status, ref loadedOnline, ref loadedStation);
        installation.OnlineStatus = loadedOnline;
        installation.StationStatus = loadedStation;
        installation.AlarmLevel = request.AlarmLevel;
        installation.AlarmMessage = alarmMessage;
        // 客户端时间可能不准（工控机不联网对时很常见），离线判定必须用服务端时间。
        installation.LastHeartbeatAt = now;
        installation.Node.LastSeenAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        var summary = InventoryMapper.ToSummary(installation, installation.Node);
        _heartbeats.Seed(appKey, installation.Id, installation.NodeId, summary, now, timestampPersisted: true);
        await _states.NotifyAsync(
            appKey, installation.Id, installation.NodeId, previous, SnapshotOf(installation), cancellationToken);
        return summary;
    }

    /// <inheritdoc />
    public async Task<InstallationSummaryDto> ReportStationStateAsync(
        string appKey,
        Guid installationId,
        DeviceStationStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DeviceStationStatuses.IsKnown(request.State))
        {
            throw BusinessException.Invalid(
                $"未知工位状态 '{request.State}'，允许值：{DeviceStationStatuses.Idle}/{DeviceStationStatuses.Online}。");
        }

        var now = _clock.UtcNow;
        if (_heartbeats.TryGet(appKey, installationId, out var cached))
        {
            InstallationSummaryDto current = cached.Summary;
            bool flipped = !string.Equals(request.State, current.StationStatus, StringComparison.Ordinal)
                           || !string.Equals(DeviceOnlineStatuses.Online, current.OnlineStatus, StringComparison.Ordinal);

            var next = current with
            {
                OnlineStatus = DeviceOnlineStatuses.Online,
                StationStatus = request.State,
                LastHeartbeatAt = now,
            };

            bool persisted = false;
            if (flipped || !_options.HeartbeatFlushEnabled)
            {
                await PersistStateAsync(
                    installationId, cached.NodeId,
                    DeviceOnlineStatuses.Online, request.State,
                    current.AlarmLevel, current.AlarmMessage, now, cancellationToken);
                persisted = true;
            }

            _heartbeats.Touch(installationId, next, now, persisted);
            if (flipped)
            {
                await _states.NotifyAsync(
                    appKey, installationId, cached.NodeId,
                    SnapshotOf(current), SnapshotOf(next), cancellationToken);
            }

            return next;
        }

        var installation = await FindAsync(appKey, installationId, cancellationToken);
        InstallationStateSnapshot previous = SnapshotOf(installation);
        installation.OnlineStatus = DeviceOnlineStatuses.Online;
        installation.StationStatus = request.State;
        installation.LastHeartbeatAt = now;
        installation.Node.LastSeenAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        var summary = InventoryMapper.ToSummary(installation, installation.Node);
        _heartbeats.Seed(appKey, installation.Id, installation.NodeId, summary, now, timestampPersisted: true);
        await _states.NotifyAsync(
            appKey, installation.Id, installation.NodeId, previous, SnapshotOf(installation), cancellationToken);
        return summary;
    }

    /// <inheritdoc />
    public async Task<int> MarkOfflineAsync(CancellationToken cancellationToken = default)
    {
        // 窄投影取候选：只查在线行的判定与事件字段，不再物化全量实体与 Node include。
        var candidates = await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(static x => x.OnlineStatus == DeviceOnlineStatuses.Online)
            .Select(static x => new ScanRow(
                x.AppKey, x.Id, x.NodeId, x.StationStatus, x.AlarmLevel,
                x.HeartbeatSeconds, x.LastHeartbeatAt))
            .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        List<ScanRow> stale = [];
        foreach (var row in candidates)
        {
            int heartbeat = row.HeartbeatSeconds > 0
                ? row.HeartbeatSeconds
                : _options.DefaultHeartbeatSeconds;
            int thresholdSeconds = Math.Max(
                Math.Max(heartbeat * _options.OfflineMultiplier, _options.MinimumOfflineSeconds),
                _options.HeartbeatFlushEnabled ? _options.HeartbeatFlushSeconds * 2 : 0);

            if (row.LastHeartbeatAt is null || row.LastHeartbeatAt < now - TimeSpan.FromSeconds(thresholdSeconds))
            {
                stale.Add(row);
            }
        }

        if (stale.Count == 0)
        {
            return 0;
        }

        Guid[] ids = [.. stale.Select(static x => x.Id)];
        HashSet<Guid>? markedIds = null;
        if (_db.Database.IsRelational())
        {
            // 条件批量更新替代逐条 SaveChanges；与并发心跳/写穿竞争的少数行会因
            // 状态位已非在线而跳过，事件按候选集发送，管理端回拉按库内事实纠偏。
            int updated = await _db.Set<DeviceInstallation>()
                .Where(x => ids.Contains(x.Id) && x.OnlineStatus == DeviceOnlineStatuses.Online)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.OnlineStatus, DeviceOnlineStatuses.Offline)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
            if (updated < ids.Length)
            {
                markedIds = await ReadStillOfflineIdsAsync(ids, now, cancellationToken);
            }
        }
        else
        {
            var rows = await _db.Set<DeviceInstallation>()
                .Where(x => ids.Contains(x.Id) && x.OnlineStatus == DeviceOnlineStatuses.Online)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.OnlineStatus = DeviceOnlineStatuses.Offline;
            }

            await _db.SaveChangesAsync(cancellationToken);
            markedIds = [.. rows.Select(static x => x.Id)];
        }

        List<InstallationStateChange> changes =
            [.. stale
                .Where(row => markedIds is null || markedIds.Contains(row.Id))
                .Select(static row => new InstallationStateChange(
                    row.AppKey, row.Id, row.NodeId,
                    new InstallationStateSnapshot(DeviceOnlineStatuses.Online, row.StationStatus, row.AlarmLevel),
                    new InstallationStateSnapshot(DeviceOnlineStatuses.Offline, row.StationStatus, row.AlarmLevel)))];
        int marked = changes.Count;
        if (marked == 0)
        {
            return 0;
        }

        // 被标离线的实例条目必须失效：其后续心跳若仍命中缓存会认为库里也在线而不写穿，
        // 回源后按设备上报值重新上线并发跃迁事件，与改造前语义一致。
        _heartbeats.RemoveRange(changes.Select(static x => x.InstallationId).ToList());
        await _states.NotifyBatchAsync(changes, cancellationToken);
        OfflineMarkedLog(_logger, marked, null);
        return marked;
    }

    /// <inheritdoc />
    public async Task MarkDisconnectedAsync(
        string appKey,
        Guid installationId,
        DateTimeOffset disconnectedAt,
        CancellationToken cancellationToken = default)
    {
        _heartbeats.Remove(installationId);

        // 注册与断开并发时，新连接可能已经落库但还没进登记表；带时间条件更新避免把 online 覆盖回 offline。
        if (_db.Database.IsRelational())
        {
            var now = _clock.UtcNow;
            // 条件更新拿不到前值，先单行读只为跃迁事件（低频路径）。错发/漏发事件不影响事实
            // （管理台始终回拉），但实例/节点 id 必须准确。
            var probe = await _db.Set<DeviceInstallation>()
                .AsNoTracking()
                .Where(x => x.AppKey == appKey
                            && x.Id == installationId
                            && x.OnlineStatus == DeviceOnlineStatuses.Online
                            && (x.LastConnectedAt == null || x.LastConnectedAt <= disconnectedAt))
                .Select(static x => new { x.NodeId, x.StationStatus, x.AlarmLevel })
                .FirstOrDefaultAsync(cancellationToken);
            int rows = await _db.Set<DeviceInstallation>()
                .Where(x => x.AppKey == appKey
                            && x.Id == installationId
                            && x.OnlineStatus == DeviceOnlineStatuses.Online
                            && (x.LastConnectedAt == null || x.LastConnectedAt <= disconnectedAt))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.OnlineStatus, DeviceOnlineStatuses.Offline)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
            if (rows > 0 && probe is not null)
            {
                await _states.NotifyAsync(
                    appKey,
                    installationId,
                    probe.NodeId,
                    new InstallationStateSnapshot(DeviceOnlineStatuses.Online, probe.StationStatus, probe.AlarmLevel),
                    new InstallationStateSnapshot(DeviceOnlineStatuses.Offline, probe.StationStatus, probe.AlarmLevel),
                    cancellationToken);
            }

            return;
        }

        var installation = await _db.Set<DeviceInstallation>()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Id == installationId,
                cancellationToken);
        if (installation is null
            || string.Equals(installation.OnlineStatus, DeviceOnlineStatuses.Offline, StringComparison.Ordinal)
            || installation.LastConnectedAt > disconnectedAt)
        {
            return;
        }

        InstallationStateSnapshot previous = SnapshotOf(installation);
        installation.OnlineStatus = DeviceOnlineStatuses.Offline;
        await _db.SaveChangesAsync(cancellationToken);
        await _states.NotifyAsync(
            appKey, installation.Id, installation.NodeId, previous, SnapshotOf(installation), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> FlushPendingHeartbeatsAsync(CancellationToken cancellationToken = default)
    {
        var batch = _heartbeats.SnapshotDirty();
        if (batch.Count == 0)
        {
            return 0;
        }

        // 统一回写批次时刻：不早于各条目的真实心跳时间，偏差上界为一个回写周期；
        // 离线阈值已按 HeartbeatFlushSeconds×2 钳制，不会因此误判在线设备。
        var now = _clock.UtcNow;
        Guid[] ids = [.. batch.Select(static x => x.InstallationId)];
        Guid[] nodeIds = [.. batch.Select(static x => x.NodeId).Distinct()];
        if (_db.Database.IsRelational())
        {
            await _db.Set<DeviceInstallation>()
                .Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.LastHeartbeatAt, now)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
            await _db.Set<DeviceNode>()
                .Where(x => nodeIds.Contains(x.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(static x => x.LastSeenAt, now),
                    cancellationToken);
        }
        else
        {
            var rows = await _db.Set<DeviceInstallation>()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.LastHeartbeatAt = now;
            }

            var nodes = await _db.Set<DeviceNode>()
                .Where(x => nodeIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var node in nodes)
            {
                node.LastSeenAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        _heartbeats.MarkFlushed(batch);
        return batch.Count;
    }

    private async Task<HashSet<Guid>> ReadStillOfflineIdsAsync(
        Guid[] ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid[] stillOffline = await _db.Set<DeviceInstallation>()
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.OnlineStatus == DeviceOnlineStatuses.Offline)
            .Select(static x => x.Id)
            .ToArrayAsync(cancellationToken);
        return [.. stillOffline];
    }

    /// <summary>缓存命中路径的单行写穿：状态三值/报警摘要 + 心跳时间直接落库。</summary>
    private async Task PersistStateAsync(
        Guid installationId,
        Guid nodeId,
        string online,
        string station,
        string alarm,
        string? alarmMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Set<DeviceInstallation>()
                .Where(x => x.Id == installationId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.OnlineStatus, online)
                        .SetProperty(static x => x.StationStatus, station)
                        .SetProperty(static x => x.AlarmLevel, alarm)
                        .SetProperty(static x => x.AlarmMessage, alarmMessage)
                        .SetProperty(static x => x.LastHeartbeatAt, now)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
            await _db.Set<DeviceNode>()
                .Where(x => x.Id == nodeId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(static x => x.LastSeenAt, now),
                    cancellationToken);
            return;
        }

        var installation = await _db.Set<DeviceInstallation>()
            .FirstOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null)
        {
            return;
        }

        installation.OnlineStatus = online;
        installation.StationStatus = station;
        installation.AlarmLevel = alarm;
        installation.AlarmMessage = alarmMessage;
        installation.LastHeartbeatAt = now;
        if (installation.Node is null)
        {
            await _db.Entry(installation).Reference(static x => x.Node).LoadAsync(cancellationToken);
        }

        if (installation.Node is not null)
        {
            installation.Node.LastSeenAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyHeartbeatStatus(string status, ref string online, ref string station)
    {
        switch (status)
        {
            case DeviceHeartbeatStatuses.Offline:
                online = DeviceOnlineStatuses.Offline;
                break;
            case DeviceHeartbeatStatuses.Idle:
                online = DeviceOnlineStatuses.Online;
                station = DeviceStationStatuses.Idle;
                break;
            case DeviceHeartbeatStatuses.Online:
                online = DeviceOnlineStatuses.Online;
                station = DeviceStationStatuses.Online;
                break;
            default:
                break;
        }
    }

    private async Task<DeviceInstallation> FindAsync(
        string appKey, Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _db.Set<DeviceInstallation>()
                                 .Include(static x => x.Node)
                                 .FirstOrDefaultAsync(
                                     x => x.AppKey == appKey && x.Id == installationId,
                                     cancellationToken)
                             ?? throw BusinessException.NotFound(
                                 ApiErrorCode.DevicesInstallationNotFound,
                                 $"安装实例 {installationId} 不存在。");
        if (!installation.Node.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesNodeDisabled,
                "物理节点已被管理端停用，停止接收心跳与工位状态。");
        }

        if (!installation.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesInstallationDisabled,
                "安装实例已被管理端停用，停止接收心跳与工位状态。");
        }

        return installation;
    }

    private sealed record ScanRow(
        string AppKey,
        Guid Id,
        Guid NodeId,
        string StationStatus,
        string AlarmLevel,
        int HeartbeatSeconds,
        DateTimeOffset? LastHeartbeatAt);
}
