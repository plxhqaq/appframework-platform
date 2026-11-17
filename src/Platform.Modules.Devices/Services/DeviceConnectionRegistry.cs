namespace Platform.Modules.Devices.Services;

/// <summary>设备连接登记：连接 id 与设备 id 的映射，用于顶号和按设备定向推送。</summary>
/// <remarks>
/// 一台机器可同时运行多个客户端安装（不同 <c>InstallGuid</c>），它们共用同一条设备记录并各自保持连接；
/// 只有同一安装的新连接会顶替旧连接，返回给 Hub 发 <c>SessionReplaced</c>。
/// </remarks>
public sealed class DeviceConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<Guid, string>> _byDevice = new();

    /// <summary>登记连接；同一设备同一安装已有其它连接时返回被顶替的连接 id。</summary>
    public string? Register(string appKey, Guid deviceId, Guid installGuid, string connectionId)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(deviceId, out var installations))
            {
                installations = new Dictionary<Guid, string>();
                _byDevice[deviceId] = installations;
            }

            string? replaced = null;
            if (installations.TryGetValue(installGuid, out var previous) && previous != connectionId)
            {
                _byConnection.Remove(previous);
                replaced = previous;
            }

            _byConnection[connectionId] = new DeviceConnection(appKey, deviceId, installGuid);
            installations[installGuid] = connectionId;
            return replaced;
        }
    }

    /// <summary>注销连接；已被同安装新连接顶替的旧连接断开时不得删除新映射。</summary>
    public void Unregister(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var connection))
            {
                return;
            }

            if (!_byDevice.TryGetValue(connection.DeviceId, out var installations))
            {
                return;
            }

            if (installations.TryGetValue(connection.InstallGuid, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                installations.Remove(connection.InstallGuid);
                if (installations.Count == 0)
                {
                    _byDevice.Remove(connection.DeviceId);
                }
            }
        }
    }

    /// <summary>当前有连接的设备数（同一设备只计一次，不看客户端安装数）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byDevice.Count;
            }
        }
    }

    /// <summary>按连接取设备归属；未注册（或已被顶替）返回 <c>false</c>。</summary>
    public bool TryGet(string connectionId, out DeviceConnection connection)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out connection!);
        }
    }

    /// <summary>设备是否还有活跃连接（任一同机客户端安装在线即为在线）。</summary>
    public bool HasConnection(Guid deviceId)
    {
        lock (_gate)
        {
            return _byDevice.ContainsKey(deviceId);
        }
    }

    /// <summary>按设备取任一连接归属；离线返回 <c>false</c>。用于命令网关复核应用隔离。</summary>
    public bool TryGetDevice(Guid deviceId, out DeviceConnection connection)
    {
        lock (_gate)
        {
            if (_byDevice.TryGetValue(deviceId, out var installations))
            {
                foreach (var connectionId in installations.Values)
                {
                    if (_byConnection.TryGetValue(connectionId, out connection!))
                    {
                        return true;
                    }
                }
            }

            connection = null!;
            return false;
        }
    }
}

/// <summary>连接归属：应用键 + 设备 id + 客户端安装 Guid。</summary>
/// <param name="AppKey">令牌绑定的应用键。</param>
/// <param name="DeviceId">设备主键。</param>
/// <param name="InstallGuid">客户端安装标识；同机不同安装互不顶号。</param>
public sealed record DeviceConnection(string AppKey, Guid DeviceId, Guid InstallGuid);
