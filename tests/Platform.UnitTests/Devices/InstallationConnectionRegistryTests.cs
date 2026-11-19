using Platform.Modules.Devices.Services;

namespace Platform.UnitTests.Devices;

/// <summary>连接登记表：顶号、重装顶替与同连接重注册的映射一致性。</summary>
public sealed class InstallationConnectionRegistryTests
{
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void 同一安装的新连接顶替旧连接()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();

        var first = registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-1");
        Assert.Empty(first.Displaced);

        var second = registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-2");

        var displaced = Assert.Single(second.Displaced);
        Assert.Equal("conn-1", displaced.ConnectionId);
        Assert.Equal(installationId, displaced.Previous.InstallationId);
        Assert.False(registry.TryGet("conn-1", out _));
        Assert.True(registry.TryGetInstallation(installationId, out var current));
        Assert.Equal(NodeId, current.NodeId);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void 同节点同应用的旧安装连接随重装作废且不影响其它应用()
    {
        var registry = new InstallationConnectionRegistry();
        var oldInstallation = Guid.NewGuid();
        var newInstallation = Guid.NewGuid();
        var otherAppInstallation = Guid.NewGuid();

        registry.Register("app-a", oldInstallation, NodeId, Guid.NewGuid(), "conn-old");
        registry.Register("app-b", otherAppInstallation, NodeId, Guid.NewGuid(), "conn-b");

        var registration = registry.Register(
            "app-a", newInstallation, NodeId, Guid.NewGuid(), "conn-new");

        var displaced = Assert.Single(registration.Displaced);
        Assert.Equal("conn-old", displaced.ConnectionId);
        Assert.Equal(oldInstallation, displaced.Previous.InstallationId);
        Assert.False(registry.HasConnection(oldInstallation));
        Assert.True(registry.HasConnection(newInstallation));
        Assert.True(registry.HasConnection(otherAppInstallation));
        Assert.Equal(2, registry.CountNodeConnections(NodeId));
    }

    [Fact]
    public void 同一连接重注册到另一安装实例时清掉旧映射()
    {
        var registry = new InstallationConnectionRegistry();
        var firstInstallation = Guid.NewGuid();
        var secondInstallation = Guid.NewGuid();

        registry.Register("app-a", firstInstallation, NodeId, Guid.NewGuid(), "conn-1");
        var registration = registry.Register(
            "app-a", secondInstallation, NodeId, Guid.NewGuid(), "conn-1");

        Assert.NotNull(registration.Previous);
        Assert.Equal(firstInstallation, registration.Previous!.Previous.InstallationId);
        Assert.Empty(registration.Displaced);
        Assert.False(registry.HasConnection(firstInstallation));
        Assert.True(registry.TryGetInstallation(secondInstallation, out var current));
        Assert.Equal(secondInstallation, current.InstallationId);
        Assert.Equal(1, registry.Count);

        // 旧连接断开不得清掉新映射。
        registry.Unregister("conn-1");
        Assert.False(registry.HasConnection(secondInstallation));
    }

    [Fact]
    public void 已顶替的旧连接断开不影响新连接()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();

        registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-old");
        registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-new");

        registry.Unregister("conn-old");

        Assert.True(registry.HasConnection(installationId));
        Assert.False(registry.TryGet("conn-old", out _));
    }

    [Fact]
    public void 应用连接数按应用隔离且顶替后旧连接不再计数()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();

        registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-old");
        registry.Register("app-b", Guid.NewGuid(), NodeId, Guid.NewGuid(), "conn-b");
        Assert.Equal(1, registry.CountAppConnections("app-a"));
        Assert.Equal(1, registry.CountAppConnections("app-b"));
        Assert.Equal(0, registry.CountAppConnections("app-c"));

        registry.Register("app-a", installationId, NodeId, Guid.NewGuid(), "conn-new");
        registry.Unregister("conn-old");
        Assert.Equal(1, registry.CountAppConnections("app-a"));
    }

    [Fact]
    public void 按机器与安装Guid定位安装实例且跨应用不误配()
    {
        var registry = new InstallationConnectionRegistry();
        var installationId = Guid.NewGuid();
        var installGuid = Guid.NewGuid();
        var otherAppInstallation = Guid.NewGuid();

        registry.Register("app-a", installationId, NodeId, installGuid, "conn-a");
        registry.Register("app-b", otherAppInstallation, NodeId, installGuid, "conn-b");

        var machineIds = registry.GetMachineInstallationIds("app-a", NodeId);
        Assert.Equal([installationId], machineIds);
        Assert.Empty(registry.GetMachineInstallationIds("app-a", Guid.NewGuid()));

        Assert.True(registry.TryGetInstallationByGuid("app-a", installGuid, out var resolved));
        Assert.Equal(installationId, resolved);
        Assert.False(registry.TryGetInstallationByGuid("app-c", installGuid, out _));
        Assert.False(registry.TryGetInstallationByGuid("app-a", Guid.NewGuid(), out _));
    }

    [Fact]
    public void 顶替重装与注销后二级索引与主表一致()
    {
        var registry = new InstallationConnectionRegistry();
        var nodeB = Guid.NewGuid();
        var inst1 = Guid.NewGuid();
        var inst2 = Guid.NewGuid();
        var inst3 = Guid.NewGuid();
        var inst4 = Guid.NewGuid();
        var guid1 = Guid.NewGuid();

        registry.Register("app-a", inst1, NodeId, guid1, "conn-1");
        registry.Register("app-b", inst2, NodeId, Guid.NewGuid(), "conn-2");
        registry.Register("app-a", inst3, nodeB, Guid.NewGuid(), "conn-3");

        registry.MarkNodeDisabled(NodeId);
        Assert.True(registry.IsKickPending("conn-1"));
        Assert.True(registry.IsKickPending("conn-2"));
        Assert.False(registry.IsKickPending("conn-3"));

        // 同节点同应用重装：inst1 的连接作废并从索引移除，inst2 不受影响。
        var registration = registry.Register("app-a", inst4, NodeId, Guid.NewGuid(), "conn-4");
        var displaced = Assert.Single(registration.Displaced);
        Assert.Equal("conn-1", displaced.ConnectionId);
        Assert.False(registry.IsKickPending("conn-1")); // 顶替即从待踢快照摘除
        Assert.Equal([inst4], registry.GetMachineInstallationIds("app-a", NodeId));
        Assert.Equal([inst2], registry.GetMachineInstallationIds("app-b", NodeId));
        Assert.Equal([inst3], registry.GetMachineInstallationIds("app-a", nodeB));
        Assert.Equal(2, registry.CountAppConnections("app-a"));
        Assert.Equal(2, registry.CountNodeConnections(NodeId));
        Assert.False(registry.TryGetInstallationByGuid("app-a", guid1, out _));

        registry.Unregister("conn-4");
        Assert.Empty(registry.GetMachineInstallationIds("app-a", NodeId));
        Assert.Equal(1, registry.CountNodeConnections(NodeId));
        Assert.True(registry.HasNodeConnection(NodeId));
        Assert.True(registry.HasNodeConnection(nodeB));
        Assert.Equal(1, registry.CountAppConnections("app-a")); // 只剩 nodeB 上的 inst3
    }
}
