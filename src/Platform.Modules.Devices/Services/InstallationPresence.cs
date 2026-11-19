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
}

/// <summary>安装实例在线状态实现。</summary>
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
    private readonly ILogger<InstallationPresence> _logger;

    /// <summary>构造。</summary>
    public InstallationPresence(
        PlatformDbContext db,
        IClock clock,
        IOptions<DevicesOptions> options,
        IInstallationStateNotifier states,
        ILogger<InstallationPresence> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _states = states;
        _logger = logger;
    }

    private static InstallationStateSnapshot SnapshotOf(DeviceInstallation installation) =>
        new(installation.OnlineStatus, installation.StationStatus, installation.AlarmLevel);

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

        var installation = await FindAsync(appKey, installationId, cancellationToken);
        InstallationStateSnapshot previous = SnapshotOf(installation);

        switch (request.Status)
        {
            case DeviceHeartbeatStatuses.Offline:
                installation.OnlineStatus = DeviceOnlineStatuses.Offline;
                break;
            case DeviceHeartbeatStatuses.Idle:
                installation.OnlineStatus = DeviceOnlineStatuses.Online;
                installation.StationStatus = DeviceStationStatuses.Idle;
                break;
            case DeviceHeartbeatStatuses.Online:
                installation.OnlineStatus = DeviceOnlineStatuses.Online;
                installation.StationStatus = DeviceStationStatuses.Online;
                break;
            default:
                break;
        }

        installation.AlarmLevel = request.AlarmLevel;
        installation.AlarmMessage = string.Equals(
            request.AlarmLevel, DeviceAlarmLevels.None, StringComparison.Ordinal)
            ? null
            : alarmMessage;
        // 客户端时间可能不准（工控机不联网对时很常见），离线判定必须用服务端时间。
        installation.LastHeartbeatAt = _clock.UtcNow;
        installation.Node.LastSeenAt = installation.LastHeartbeatAt;

        await _db.SaveChangesAsync(cancellationToken);
        await _states.NotifyAsync(
            appKey, installation.Id, installation.NodeId, previous, SnapshotOf(installation), cancellationToken);
        return InventoryMapper.ToSummary(installation, installation.Node);
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

        var installation = await FindAsync(appKey, installationId, cancellationToken);
        InstallationStateSnapshot previous = SnapshotOf(installation);
        installation.OnlineStatus = DeviceOnlineStatuses.Online;
        installation.StationStatus = request.State;
        installation.LastHeartbeatAt = _clock.UtcNow;
        installation.Node.LastSeenAt = installation.LastHeartbeatAt;

        await _db.SaveChangesAsync(cancellationToken);
        await _states.NotifyAsync(
            appKey, installation.Id, installation.NodeId, previous, SnapshotOf(installation), cancellationToken);
        return InventoryMapper.ToSummary(installation, installation.Node);
    }

    /// <inheritdoc />
    public async Task<int> MarkOfflineAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _db.Set<DeviceInstallation>()
            .Where(x => x.OnlineStatus == DeviceOnlineStatuses.Online)
            .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        int marked = 0;
        List<InstallationStateChange> changes = [];
        foreach (var installation in candidates)
        {
            int heartbeat = installation.HeartbeatSeconds > 0
                ? installation.HeartbeatSeconds
                : _options.DefaultHeartbeatSeconds;
            var threshold = TimeSpan.FromSeconds(
                Math.Max(heartbeat * _options.OfflineMultiplier, _options.MinimumOfflineSeconds));

            if (installation.LastHeartbeatAt is null || installation.LastHeartbeatAt < now - threshold)
            {
                InstallationStateSnapshot previous = SnapshotOf(installation);
                installation.OnlineStatus = DeviceOnlineStatuses.Offline;
                changes.Add(new InstallationStateChange(
                    installation.AppKey, installation.Id, installation.NodeId,
                    previous, SnapshotOf(installation)));
                marked++;
            }
        }

        if (marked > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _states.NotifyBatchAsync(changes, cancellationToken);
            OfflineMarkedLog(_logger, marked, null);
        }

        return marked;
    }

    /// <inheritdoc />
    public async Task MarkDisconnectedAsync(
        string appKey,
        Guid installationId,
        DateTimeOffset disconnectedAt,
        CancellationToken cancellationToken = default)
    {
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
}
