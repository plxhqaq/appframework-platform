namespace Platform.Kernel.Abstractions;

/// <summary>
/// 安装实例事件路由：业务模块经此向设备 Hub 发布事件，不直接持有 <c>IHubContext&lt;DeviceHub&gt;</c>。
/// 实现位于 Devices 模块（依赖进程内连接登记表）；消费方以 <c>IEnumerable&lt;T&gt;</c> 取可选依赖，
/// Devices 模块缺席时推送静默跳过（推送只是提示，事实以回拉为准）。
/// </summary>
public interface IInstallationEventRouter
{
    /// <summary>定向到单个安装实例（校验 appKey 归属，不匹配则跳过）。</summary>
    Task ToInstallationAsync(
        string appKey,
        Guid installationId,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>定向到目标机器上该应用的全部活跃安装实例（授权变更路由：machineCode → 节点 → 连接）。</summary>
    Task ToMachineAsync(
        string appKey,
        string machineCode,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>按客户端安装标识定向（扫码审批等已知 installGuid 的场景）。</summary>
    Task ToInstallGuidAsync(
        string appKey,
        Guid installGuid,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>应用组广播：该应用全部注册连接。</summary>
    Task ToAppAsync(
        string appKey,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>节点组广播：该节点全部安装实例（跨应用）。</summary>
    Task ToNodeAsync(
        Guid nodeId,
        string eventName,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>应用当前活跃连接数（应用组广播的在线门禁）。</summary>
    int AppConnectionCount(string appKey);
}