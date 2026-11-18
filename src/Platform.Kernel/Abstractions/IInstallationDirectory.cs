namespace Platform.Kernel.Abstractions;

/// <summary>
/// 安装实例目录：其它模块校验应用归属与补充展示信息，不引用 Devices 实体。
/// 由 Devices 模块实现；Devices 缺席时调用方应返回明确的"设备域未启用"。
/// </summary>
public interface IInstallationDirectory
{
    /// <summary>按应用读取安装实例；不存在或不属于该应用时返回 <c>null</c>。</summary>
    Task<InstallationDirectoryEntry?> FindAsync(
        string appKey,
        Guid installationId,
        CancellationToken cancellationToken = default);

    /// <summary>按应用+节点取最近心跳的安装实例；不存在返回 <c>null</c>。</summary>
    Task<InstallationDirectoryEntry?> FindByNodeAsync(
        string appKey,
        Guid nodeId,
        CancellationToken cancellationToken = default);
}

/// <summary>安装实例目录条目：只暴露调用方需要的展示字段。</summary>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="NodeId">物理节点主键。</param>
/// <param name="AppKey">所属应用键。</param>
/// <param name="NodeDisplayName">节点展示名。</param>
public sealed record InstallationDirectoryEntry(
    Guid InstallationId,
    Guid NodeId,
    string AppKey,
    string NodeDisplayName);
