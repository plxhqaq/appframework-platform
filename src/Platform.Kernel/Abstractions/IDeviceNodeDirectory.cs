namespace Platform.Kernel.Abstractions;

/// <summary>
/// 物理节点目录：其它模块按节点 id 或机器码补充展示信息 / 解析机器码，不引用 Devices 实体。
/// 由 Devices 模块实现；Devices 缺席时按节点签发授权必须明确失败。
/// </summary>
public interface IDeviceNodeDirectory
{
    /// <summary>按节点主键读取；不存在返回 <c>null</c>。</summary>
    Task<DeviceNodeDirectoryEntry?> FindAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>按机器码读取；不存在返回 <c>null</c>。</summary>
    Task<DeviceNodeDirectoryEntry?> FindByMachineCodeAsync(
        string machineCode,
        CancellationToken cancellationToken = default);

    /// <summary>按机器码批量读取（会话/授权列表补充节点信息用）。</summary>
    Task<IReadOnlyDictionary<string, DeviceNodeDirectoryEntry>> FindByMachineCodesAsync(
        IReadOnlyCollection<string> machineCodes,
        CancellationToken cancellationToken = default);
}

/// <summary>节点目录条目：只暴露调用方需要的展示字段。</summary>
/// <param name="NodeId">物理节点主键。</param>
/// <param name="MachineCode">规范化机器码。</param>
/// <param name="DisplayName">节点展示名。</param>
public sealed record DeviceNodeDirectoryEntry(
    Guid NodeId,
    string MachineCode,
    string DisplayName);
