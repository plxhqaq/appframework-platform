namespace Platform.Modules.Devices.Services;

/// <summary>安装实例连接登记：连接 id 与安装实例的映射，用于顶号、定向推送与节点级下发。</summary>
/// <remarks>
/// 同一安装实例的新连接会顶替旧连接；同一节点同一应用的旧安装（重装产生的新安装实例）也会被顶替；
/// 同机不同应用（不同安装实例）互不顶号。
/// </remarks>
public sealed class InstallationConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InstallationConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byInstallation = new();
    private readonly HashSet<string> _kickPending = new(StringComparer.Ordinal);

    /// <summary>登记连接；返回需要移出组并通知被顶替的旧连接。</summary>
    public InstallationRegistration Register(
        string appKey,
        Guid installationId,
        Guid nodeId,
        Guid installGuid,
        string connectionId)
    {
        lock (_gate)
        {
            // 新登记即视为干净连接；待踢标记只针对停用瞬间快照的旧连接。
            _kickPending.Remove(connectionId);

            ReplacedConnection? previous = null;
            var displaced = new List<ReplacedConnection>();

            if (_byConnection.Remove(connectionId, out var existing))
            {
                if (_byInstallation.TryGetValue(existing.InstallationId, out var mapped)
                    && string.Equals(mapped, connectionId, StringComparison.Ordinal))
                {
                    _byInstallation.Remove(existing.InstallationId);
                }

                // 同一连接重注册到同一安装：组不变，也不需要通知自己被顶替。
                if (existing.InstallationId != installationId)
                {
                    previous = new ReplacedConnection(connectionId, existing);
                }
            }

            // 同一安装实例的旧连接：新连接顶替。
            if (_byInstallation.TryGetValue(installationId, out var current)
                && !string.Equals(current, connectionId, StringComparison.Ordinal)
                && _byConnection.Remove(current, out var replaced))
            {
                _kickPending.Remove(current);
                displaced.Add(new ReplacedConnection(current, replaced));
            }

            // 同一节点同一应用的其它安装实例（应用重装）：旧安装的连接随新安装作废。
            foreach (var (otherInstallation, otherConnection) in _byInstallation.ToArray())
            {
                if (otherInstallation == installationId
                    || string.Equals(otherConnection, connectionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (_byConnection.TryGetValue(otherConnection, out var other)
                    && other.NodeId == nodeId
                    && string.Equals(other.AppKey, appKey, StringComparison.Ordinal))
                {
                    _byConnection.Remove(otherConnection);
                    _byInstallation.Remove(otherInstallation);
                    _kickPending.Remove(otherConnection);
                    displaced.Add(new ReplacedConnection(otherConnection, other));
                }
            }

            _byConnection[connectionId] = new InstallationConnection(
                appKey, installationId, nodeId, installGuid);
            _byInstallation[installationId] = connectionId;
            return new InstallationRegistration(previous, displaced);
        }
    }

    /// <summary>注销连接；已被同安装新连接顶替的旧连接断开时不得删除新映射。</summary>
    public void Unregister(string connectionId)
    {
        lock (_gate)
        {
            _kickPending.Remove(connectionId);
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

    /// <summary>应用当前活跃连接数（应用组广播的在线门禁）。</summary>
    public int CountAppConnections(string appKey)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (var connectionId in _byInstallation.Values)
            {
                if (_byConnection.TryGetValue(connectionId, out var connection)
                    && string.Equals(connection.AppKey, appKey, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>目标机器上该应用的全部活跃安装实例（授权变更定向路由）。</summary>
    public IReadOnlyList<Guid> GetMachineInstallationIds(string appKey, Guid nodeId)
    {
        lock (_gate)
        {
            var ids = new List<Guid>();
            foreach (var (installationId, connectionId) in _byInstallation)
            {
                if (_byConnection.TryGetValue(connectionId, out var connection)
                    && connection.NodeId == nodeId
                    && string.Equals(connection.AppKey, appKey, StringComparison.Ordinal))
                {
                    ids.Add(installationId);
                }
            }

            return ids;
        }
    }

    /// <summary>按客户端安装标识取安装实例（扫码审批定向）；同应用同安装只有一条活跃连接。</summary>
    public bool TryGetInstallationByGuid(string appKey, Guid installGuid, out Guid installationId)
    {
        lock (_gate)
        {
            foreach (var (candidateId, connectionId) in _byInstallation)
            {
                if (_byConnection.TryGetValue(connectionId, out var connection)
                    && connection.InstallGuid == installGuid
                    && string.Equals(connection.AppKey, appKey, StringComparison.Ordinal))
                {
                    installationId = candidateId;
                    return true;
                }
            }

            installationId = default;
            return false;
        }
    }

    /// <summary>
    /// 停用安装实例：快照其当前连接进"待踢"集合。之后的每次上行调用会被
    /// <c>DeviceHubGuardFilter</c> 强制断线；不在此集合建立后新到的连接（重连由注册路径 807 拒绝）。
    /// </summary>
    public void MarkInstallationDisabled(Guid installationId)
    {
        lock (_gate)
        {
            if (_byInstallation.TryGetValue(installationId, out var connectionId))
            {
                _kickPending.Add(connectionId);
            }
        }
    }

    /// <summary>停用物理节点：快照该节点全部应用的活跃连接进"待踢"集合（重连由注册路径 806 拒绝）。</summary>
    public void MarkNodeDisabled(Guid nodeId)
    {
        lock (_gate)
        {
            foreach (var connectionId in _byInstallation.Values)
            {
                if (_byConnection.TryGetValue(connectionId, out var connection)
                    && connection.NodeId == nodeId)
                {
                    _kickPending.Add(connectionId);
                }
            }
        }
    }

    /// <summary>连接是否属于停用快照（待踢）。</summary>
    public bool IsKickPending(string connectionId)
    {
        lock (_gate)
        {
            return _kickPending.Contains(connectionId);
        }
    }
}

/// <summary>登记结果：调用方需要把这里的连接移出旧组，并通知其已被顶替。</summary>
/// <param name="Previous">同一连接 id 的旧登记（重注册到另一安装实例），只需移组。</param>
/// <param name="Displaced">被新连接顶替的其它连接（同安装旧连接、同节点同应用旧安装）。</param>
public sealed record InstallationRegistration(
    ReplacedConnection? Previous,
    IReadOnlyList<ReplacedConnection> Displaced);

/// <summary>被顶替的连接：连接 id + 原登记归属。</summary>
/// <param name="ConnectionId">SignalR 连接 id。</param>
/// <param name="Previous">原登记归属。</param>
public sealed record ReplacedConnection(string ConnectionId, InstallationConnection Previous);

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
