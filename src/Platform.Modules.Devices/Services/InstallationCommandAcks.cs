using System.Collections.Concurrent;
using Platform.Contracts.Devices;

namespace Platform.Modules.Devices.Services;

/// <summary>配置下发回执的等待表：命令网关在这里等，Hub 收到 Ack 后完成等待。</summary>
/// <remarks>键包含安装实例 id：同应用下其它安装实例不能替目标实例回执。</remarks>
public interface IInstallationCommandAcks
{
    /// <summary>登记一次等待；同一安装实例同一下发只允许一个等待者。</summary>
    TaskCompletionSource<DeviceConfigPushAck> Begin(string appKey, Guid installationId, Guid backupId);

    /// <summary>完成等待；没有等待者时返回 <c>false</c>（例如超时后才到的回执）。</summary>
    bool Complete(string appKey, Guid installationId, DeviceConfigPushAck ack);

    /// <summary>放弃等待（超时/发送失败），避免表无限增长。</summary>
    void Abandon(string appKey, Guid installationId, Guid backupId);
}

/// <summary>回执等待表的进程内实现；多副本部署时下发与回执可能落到不同副本，需要换带背板的实现。</summary>
public sealed class InstallationCommandAcks : IInstallationCommandAcks
{
    private readonly ConcurrentDictionary<(string AppKey, Guid InstallationId, Guid BackupId), TaskCompletionSource<DeviceConfigPushAck>> _pending = new();

    /// <inheritdoc />
    public TaskCompletionSource<DeviceConfigPushAck> Begin(string appKey, Guid installationId, Guid backupId) =>
        _pending.GetOrAdd(
            (appKey, installationId, backupId),
            static _ => new TaskCompletionSource<DeviceConfigPushAck>(
                TaskCreationOptions.RunContinuationsAsynchronously));

    /// <inheritdoc />
    public bool Complete(string appKey, Guid installationId, DeviceConfigPushAck ack) =>
        _pending.TryRemove((appKey, installationId, ack.BackupId), out var pending)
        && pending.TrySetResult(ack);

    /// <inheritdoc />
    public void Abandon(string appKey, Guid installationId, Guid backupId) =>
        _pending.TryRemove((appKey, installationId, backupId), out _);
}
