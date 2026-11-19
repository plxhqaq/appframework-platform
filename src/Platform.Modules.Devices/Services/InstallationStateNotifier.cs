using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts.Hubs;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>安装实例三值状态快照（在线/工位/报警），跃迁判定的最小单元。</summary>
/// <param name="OnlineStatus">在线状态。</param>
/// <param name="StationStatus">工位状态。</param>
/// <param name="AlarmLevel">报警级别。</param>
public sealed record InstallationStateSnapshot(
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel);

/// <summary>一次安装实例状态变更：跃迁前后快照成对给出，值相同则视为静默。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="NodeId">所属节点主键。</param>
/// <param name="Previous">落库前的三值快照。</param>
/// <param name="Current">落库后的三值快照。</param>
public sealed record InstallationStateChange(
    string AppKey,
    Guid InstallationId,
    Guid NodeId,
    InstallationStateSnapshot Previous,
    InstallationStateSnapshot Current);

/// <summary>
/// 安装实例/节点聚合状态跃迁提示：仅在值变化时向 AdminHub devices 域发事件。
/// 推送只是提示——发布失败只记日志，事实以管理端接口与 60s 兜底回拉为准。
/// </summary>
public interface IInstallationStateNotifier
{
    /// <summary>单实例跃迁；前后快照相同则直接返回，不产生任何查询。</summary>
    Task NotifyAsync(
        string appKey,
        Guid installationId,
        Guid nodeId,
        InstallationStateSnapshot previous,
        InstallationStateSnapshot current,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量跃迁（离线扫描等）：每实例至多一条 InstallationStateChanged，
    /// 每节点至多一条聚合 NodeStateChanged（聚合新旧对比只查库一次/节点）。
    /// </summary>
    Task NotifyBatchAsync(
        IReadOnlyList<InstallationStateChange> changes,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class InstallationStateNotifier : IInstallationStateNotifier
{
    private readonly PlatformDbContext _db;
    private readonly IAdminEventPublisher _adminEvents;
    private readonly IClock _clock;
    private readonly ILogger<InstallationStateNotifier> _logger;

    /// <summary>构造。</summary>
    public InstallationStateNotifier(
        PlatformDbContext db,
        IAdminEventPublisher adminEvents,
        IClock clock,
        ILogger<InstallationStateNotifier> logger)
    {
        _db = db;
        _adminEvents = adminEvents;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyAsync(
        string appKey,
        Guid installationId,
        Guid nodeId,
        InstallationStateSnapshot previous,
        InstallationStateSnapshot current,
        CancellationToken cancellationToken = default) =>
        NotifyBatchAsync(
            [new InstallationStateChange(appKey, installationId, nodeId, previous, current)],
            cancellationToken);

    /// <inheritdoc />
    public async Task NotifyBatchAsync(
        IReadOnlyList<InstallationStateChange> changes,
        CancellationToken cancellationToken = default)
    {
        List<InstallationStateChange> flipped =
        [
            .. changes
                .Where(static x => !string.Equals(x.Previous.OnlineStatus, x.Current.OnlineStatus, StringComparison.Ordinal)
                                   || !string.Equals(x.Previous.StationStatus, x.Current.StationStatus, StringComparison.Ordinal)
                                   || !string.Equals(x.Previous.AlarmLevel, x.Current.AlarmLevel, StringComparison.Ordinal))
                .GroupBy(static x => x.InstallationId)
                .Select(static g => g.Last()),
        ];
        if (flipped.Count == 0)
        {
            return;
        }

        try
        {
            Guid[] nodeIds = [.. flipped.Select(static x => x.NodeId).Distinct()];
            // 节点聚合需要全量行；只走跃迁路径，心跳值不变的静默场景一次查询都不做。
            List<StateRow> rows = await _db.Set<DeviceInstallation>()
                .AsNoTracking()
                .Where(x => nodeIds.Contains(x.NodeId))
                .Select(static x => new StateRow(
                    x.Id, x.NodeId, x.OnlineStatus, x.StationStatus, x.AlarmLevel, x.LastHeartbeatAt))
                .ToListAsync(cancellationToken);
            Dictionary<Guid, List<StateRow>> byNode = rows
                .GroupBy(static x => x.NodeId)
                .ToDictionary(static g => g.Key, static g => g.ToList());
            var now = _clock.UtcNow;

            foreach (var change in flipped)
            {
                await _adminEvents.PublishAsync(
                    AdminHubDomains.Devices,
                    AdminHubEvents.InstallationStateChanged,
                    new AdminInstallationStateChangedNotification(
                        change.AppKey,
                        change.InstallationId,
                        change.NodeId,
                        change.Current.OnlineStatus,
                        change.Current.StationStatus,
                        change.Current.AlarmLevel,
                        now),
                    cancellationToken);
            }

            foreach (var nodeId in nodeIds)
            {
                if (!byNode.TryGetValue(nodeId, out List<StateRow>? nodeRows) || nodeRows.Count == 0)
                {
                    continue;
                }

                Dictionary<Guid, InstallationStateSnapshot> previousByInstallation =
                    flipped
                        .Where(x => x.NodeId == nodeId)
                        .GroupBy(static x => x.InstallationId)
                        .ToDictionary(static g => g.Key, static g => g.Last().Previous);
                List<InventoryMapper.InstallationStateView> newViews =
                    [.. nodeRows.Select(static r => r.ToView())];
                List<InventoryMapper.InstallationStateView> oldViews =
                    [.. nodeRows.Select(r => previousByInstallation.TryGetValue(r.Id, out InstallationStateSnapshot? previous)
                        ? new InventoryMapper.InstallationStateView(
                            previous.OnlineStatus, previous.StationStatus, previous.AlarmLevel, null, r.LastHeartbeatAt)
                        : r.ToView())];

                InventoryMapper.NodeState newState = InventoryMapper.AggregateViews(newViews);
                InventoryMapper.NodeState oldState = InventoryMapper.AggregateViews(oldViews);
                if (string.Equals(oldState.OnlineStatus, newState.OnlineStatus, StringComparison.Ordinal)
                    && string.Equals(oldState.StationStatus, newState.StationStatus, StringComparison.Ordinal)
                    && string.Equals(oldState.AlarmLevel, newState.AlarmLevel, StringComparison.Ordinal))
                {
                    continue;
                }

                await _adminEvents.PublishAsync(
                    AdminHubDomains.Devices,
                    AdminHubEvents.NodeStateChanged,
                    new AdminNodeStateChangedNotification(
                        nodeId,
                        newState.OnlineStatus,
                        newState.StationStatus,
                        newState.AlarmLevel,
                        now),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "状态跃迁事件发布失败：{Count} 项变更（事实已落库，管理台兜底回拉）。",
                flipped.Count);
        }
    }

    private sealed record StateRow(
        Guid Id,
        Guid NodeId,
        string OnlineStatus,
        string StationStatus,
        string AlarmLevel,
        DateTimeOffset? LastHeartbeatAt)
    {
        public InventoryMapper.InstallationStateView ToView() =>
            new(OnlineStatus, StationStatus, AlarmLevel, null, LastHeartbeatAt);
    }
}
