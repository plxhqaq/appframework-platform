namespace Platform.Modules.Devices.Services;

/// <summary>设备连接登记：连接 id 与设备 id 的双向映射，用于顶号和按设备定向推送。</summary>
/// <remarks>只保存当前活连接；同一设备再次注册时旧连接被顶替，返回给 Hub 发 <c>SessionReplaced</c>。</remarks>
public sealed class DeviceConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byDevice = new();

    /// <summary>登记连接；同一设备已有其它连接时返回被顶替的连接 id。</summary>
    public string? Register(string appKey, Guid deviceId, string connectionId)
    {
        lock (_gate)
        {
            string? replaced = null;
            if (_byDevice.TryGetValue(deviceId, out var previous) && previous != connectionId)
            {
                _byConnection.Remove(previous);
                replaced = previous;
            }

            _byConnection[connectionId] = new DeviceConnection(appKey, deviceId);
            _byDevice[deviceId] = connectionId;
            return replaced;
        }
    }

    /// <summary>注销连接；已被新连接顶替的旧连接断开时不得删除新映射。</summary>
    public void Unregister(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var connection))
            {
                return;
            }

            if (_byDevice.TryGetValue(connection.DeviceId, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                _byDevice.Remove(connection.DeviceId);
            }
        }
    }

    /// <summary>当前实时连接数（同一设备只计一次）。</summary>
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

    /// <summary>设备当前连接 id；离线返回 <c>null</c>。</summary>
    public string? GetConnectionId(Guid deviceId)
    {
        lock (_gate)
        {
            return _byDevice.TryGetValue(deviceId, out var connectionId) ? connectionId : null;
        }
    }

    /// <summary>按设备取连接归属；离线返回 <c>false</c>。用于命令网关复核应用隔离。</summary>
    public bool TryGetDevice(Guid deviceId, out DeviceConnection connection)
    {
        lock (_gate)
        {
            if (_byDevice.TryGetValue(deviceId, out var connectionId)
                && _byConnection.TryGetValue(connectionId, out connection!))
            {
                return true;
            }

            connection = null!;
            return false;
        }
    }
}

/// <summary>连接归属：应用键 + 设备 id。</summary>
/// <param name="AppKey">令牌绑定的应用键。</param>
/// <param name="DeviceId">设备主键。</param>
public sealed record DeviceConnection(string AppKey, Guid DeviceId);
