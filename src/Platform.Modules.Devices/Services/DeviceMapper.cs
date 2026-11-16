using Platform.Contracts.Devices;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Services;

/// <summary>设备实体 → 通用 DTO 的唯一映射点。</summary>
internal static class DeviceMapper
{
    public static DeviceSummaryDto ToSummary(Device device) => new(
        device.Id,
        DeviceIdentityHasher.ShortId(device.DeviceKey),
        device.MachineCode,
        device.DisplayName,
        device.AutoName,
        device.CustomName,
        device.IpAddress,
        device.Branch,
        device.FrameworkVersion,
        device.OnlineStatus,
        device.StationStatus,
        device.AlarmLevel,
        device.AlarmMessage,
        device.LastConnectedAt,
        device.LastHeartbeatAt,
        device.HeartbeatSeconds,
        device.IsEnabled);

    public static DeviceDetailDto ToDetail(Device device) => new(
        device.Id,
        device.DeviceKey,
        DeviceIdentityHasher.ShortId(device.DeviceKey),
        device.MachineCode,
        device.InstallGuid,
        device.DisplayName,
        device.AutoName,
        device.CustomName,
        device.IpAddress,
        device.Branch,
        device.FrameworkVersion,
        device.OnlineStatus,
        device.StationStatus,
        device.AlarmLevel,
        device.AlarmMessage,
        device.FirstConnectedAt,
        device.LastConnectedAt,
        device.LastHeartbeatAt,
        device.HeartbeatSeconds,
        device.IsEnabled,
        device.CreatedAt,
        device.UpdatedAt);
}
