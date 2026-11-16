namespace Platform.Kernel.Abstractions;

/// <summary>
/// 设备目录：其它模块校验设备归属与补充展示信息，不引用 Devices 实体。
/// 由 Devices 模块实现；Devices 缺席时调用方应返回明确的"设备域未启用"。
/// </summary>
public interface IDeviceDirectory
{
    /// <summary>按应用读取设备；不存在或不属于该应用时返回 <c>null</c>。</summary>
    Task<DeviceDirectoryEntry?> FindAsync(
        string appKey,
        Guid deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>设备是否存在（不区分应用）；仅用于区分"不存在"与"跨应用"。</summary>
    Task<bool> ExistsAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

/// <summary>设备目录条目：只暴露调用方需要的展示字段。</summary>
/// <param name="DeviceId">设备主键。</param>
/// <param name="DisplayName">展示名。</param>
public sealed record DeviceDirectoryEntry(
    Guid DeviceId,
    string DisplayName);
