using Platform.Contracts.Devices;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>节点/安装实例实体 → 通用 DTO 的唯一映射点。</summary>
internal static class InventoryMapper
{
    public static InstallationSummaryDto ToSummary(DeviceInstallation installation, DeviceNode node) => new(
        installation.Id,
        node.Id,
        node.DisplayName,
        installation.DisplayName,
        installation.ClientName,
        installation.CustomName,
        installation.AppKey,
        node.MachineCode,
        DeviceIdentityHasher.ShortId(installation.Id),
        installation.InstallGuid,
        installation.Branch,
        installation.FrameworkVersion,
        installation.OnlineStatus,
        installation.StationStatus,
        installation.AlarmLevel,
        installation.AlarmMessage,
        installation.LastConnectedAt,
        installation.LastHeartbeatAt,
        installation.HeartbeatSeconds,
        installation.IsEnabled,
        node.IsEnabled);

    public static InstallationDetailDto ToDetail(DeviceInstallation installation, DeviceNode node) => new(
        installation.Id,
        node.Id,
        node.DisplayName,
        installation.DisplayName,
        installation.ClientName,
        installation.CustomName,
        installation.AppKey,
        node.MachineCode,
        DeviceIdentityHasher.ShortId(installation.Id),
        installation.InstallGuid,
        installation.Branch,
        installation.FrameworkVersion,
        installation.OnlineStatus,
        installation.StationStatus,
        installation.AlarmLevel,
        installation.AlarmMessage,
        installation.FirstConnectedAt,
        installation.LastConnectedAt,
        installation.LastHeartbeatAt,
        installation.HeartbeatSeconds,
        installation.IsEnabled,
        node.IsEnabled,
        installation.CreatedAt,
        installation.UpdatedAt);

    public static NodeSummaryDto ToSummary(DeviceNode node, IReadOnlyList<DeviceInstallation> installations)
    {
        var state = Aggregate(installations);
        return new NodeSummaryDto(
            node.Id,
            DeviceIdentityHasher.ShortId(node.Id),
            node.MachineCode,
            node.DisplayName,
            node.AutoName,
            node.CustomName,
            node.IpAddress,
            state.OnlineStatus,
            state.StationStatus,
            state.AlarmLevel,
            state.AlarmMessage,
            installations.Count,
            installations.Count(static x => x.OnlineStatus == DeviceOnlineStatuses.Online),
            [.. installations
                .Select(static x => x.AppKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal)],
            node.FirstSeenAt,
            node.LastSeenAt,
            state.LastHeartbeatAt,
            node.IsEnabled);
    }

    public static NodeDetailDto ToDetail(DeviceNode node, IReadOnlyList<DeviceInstallation> installations)
    {
        var state = Aggregate(installations);
        return new NodeDetailDto(
            node.Id,
            DeviceIdentityHasher.ShortId(node.Id),
            node.MachineCode,
            node.DisplayName,
            node.AutoName,
            node.CustomName,
            node.IpAddress,
            state.OnlineStatus,
            state.StationStatus,
            state.AlarmLevel,
            state.AlarmMessage,
            node.IsEnabled,
            node.FirstSeenAt,
            node.LastSeenAt,
            state.LastHeartbeatAt,
            node.CreatedAt,
            node.UpdatedAt,
            [.. installations
                .OrderBy(static x => x.AppKey, StringComparer.Ordinal)
                .ThenByDescending(static x => x.LastHeartbeatAt)
                .Select(x => ToSummary(x, node))]);
    }

    private static NodeState Aggregate(IReadOnlyList<DeviceInstallation> installations)
    {
        bool online = false;
        bool running = false;
        DateTimeOffset? lastHeartbeat = null;
        DeviceInstallation? alarmSource = null;
        int alarmSeverity = 0;

        foreach (var installation in installations)
        {
            bool isOnline = installation.OnlineStatus == DeviceOnlineStatuses.Online;
            online |= isOnline;
            running |= isOnline && installation.StationStatus == DeviceStationStatuses.Online;

            if (installation.LastHeartbeatAt is { } heartbeat
                && (lastHeartbeat is null || heartbeat > lastHeartbeat))
            {
                lastHeartbeat = heartbeat;
            }

            int severity = DeviceAlarmLevels.Severity(installation.AlarmLevel);
            if (severity > alarmSeverity
                || (severity == alarmSeverity
                    && severity > 0
                    && installation.LastHeartbeatAt > alarmSource?.LastHeartbeatAt))
            {
                alarmSeverity = severity;
                alarmSource = installation;
            }
        }

        string onlineStatus = online ? DeviceOnlineStatuses.Online : DeviceOnlineStatuses.Offline;
        string stationStatus = running ? DeviceStationStatuses.Online : DeviceStationStatuses.Idle;
        return new NodeState(
            onlineStatus,
            stationStatus,
            alarmSource?.AlarmLevel ?? DeviceAlarmLevels.None,
            alarmSource?.AlarmMessage,
            lastHeartbeat);
    }

    private sealed record NodeState(
        string OnlineStatus,
        string StationStatus,
        string AlarmLevel,
        string? AlarmMessage,
        DateTimeOffset? LastHeartbeatAt);
}
