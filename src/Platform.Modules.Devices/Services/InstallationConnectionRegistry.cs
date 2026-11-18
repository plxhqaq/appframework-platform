namespace Platform.Modules.Devices.Services;

/// <summary>安装实例连接登记：连接 id 与安装实例的映射，用于顶号、定向推送与节点级下发。</summary>
/// <remarks>
/// 同一安装实例的新连接会顶替旧连接；同机不同应用（不同安装实例）互不顶号。
/// </remarks>
public sealed class InstallationConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InstallationConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byInstallation = new();

    /// <summary>登记连接；同一安装实例已有其它连接时返回被顶替的连接 id。</summary>
    public string? Register(
        string appKey,
        Guid installationId,
        Guid nodeId,
        Guid installGuid,
        string connectionId)
    {
        lock (_gate)
        {
            string? replaced = null;
            if (_byInstallation.TryGetValue(installationId, out var previous) && previous != connectionId)
            {
                _byConnection.Remove(previous);
                replaced = previous;
            }

            _byConnection[connectionId] = new InstallationConnection(
                appKey, installationId, nodeId, installGuid);
            _byInstallation[installationId] = connectionId;
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

            if (_byInstallation.TryGetValue(connection.InstallationId, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                _byInstallation.Remove(connection.InstallationId);
            }
        }
    }

    /// <summary>当前有连接的安装实例数（同机多应用分别计数）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byInstallation.Count;
            }
        }
    }

    /// <summary>按连接取安装实例归属；未注册（或已被顶替）返回 <c>false</c>。</summary>
    public bool TryGet(string connectionId, out InstallationConnection connection)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out connection!);
        }
    }

    /// <summary>安装实例是否还有活跃连接。</summary>
    public bool HasConnection(Guid installationId)
    {
        lock (_gate)
        {
            return _byInstallation.ContainsKey(installationId);
        }
    }

    /// <summary>节点上是否还有任一安装实例连接。</summary>
    public bool HasNodeConnection(Guid nodeId)
    {
        lock (_gate)
        {
            return CountNodeConnectionsCore(nodeId) > 0;
        }
    }

    /// <summary>节点上当前有连接的安装实例数。</summary>
    public int CountNodeConnections(Guid nodeId)
    {
        lock (_gate)
        {
            return CountNodeConnectionsCore(nodeId);
        }
    }

    private int CountNodeConnectionsCore(Guid nodeId)
    {
        int count = 0;
        foreach (var connectionId in _byInstallation.Values)
        {
            if (_byConnection.TryGetValue(connectionId, out var connection)
                && connection.NodeId == nodeId)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>按安装实例取连接归属；离线返回 <c>false</c>。</summary>
    public bool TryGetInstallation(Guid installationId, out InstallationConnection connection)
    {
        lock (_gate)
        {
            if (_byInstallation.TryGetValue(installationId, out var connectionId)
                && _byConnection.TryGetValue(connectionId, out connection!))
            {
                return true;
            }

            connection = null!;
            return false;
        }
    }
}

/// <summary>连接归属：应用键 + 安装实例 + 物理节点 + 客户端安装 Guid。</summary>
/// <param name="AppKey">令牌绑定的应用键。</param>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="NodeId">物理节点主键。</param>
/// <param name="InstallGuid">客户端安装标识。</param>
public sealed record InstallationConnection(
    string AppKey,
    Guid InstallationId,
    Guid NodeId,
    Guid InstallGuid);
