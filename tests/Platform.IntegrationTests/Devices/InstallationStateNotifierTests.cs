using Microsoft.Extensions.Logging.Abstractions;
using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Services;

namespace Platform.IntegrationTests.Devices;

/// <summary>
/// W4 状态跃迁通知器的差分决策表：值不变全静默、实例事件逐条、
/// 节点聚合「新旧对比」只在变化时恰好一条；发布异常只吞不抛。
/// </summary>
public sealed class InstallationStateNotifierTests
{
    private static InstallationStateSnapshot Snap(string online, string station, string alarm) =>
        new(online, station, alarm);

    private static DeviceNode NewNode(string machineCode) =>
        new() { MachineCode = machineCode, DisplayName = machineCode };

    private static DeviceInstallation NewInstallation(
        DeviceNode node, string appKey, string online, string station, string alarm) =>
        new()
        {
            AppKey = appKey,
            NodeId = node.Id,
            Node = node,
            OnlineStatus = online,
            StationStatus = station,
            AlarmLevel = alarm,
        };

    private static InstallationStateNotifier CreateNotifier(
        PlatformDbContext db, IAdminEventPublisher admin, IClock clock) =>
        new(db, admin, clock, NullLogger<InstallationStateNotifier>.Instance);

    [Fact]
    public async Task 快照三值相同完全静默()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-A");
        var installation = NewInstallation(node, "demo", DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().Add(installation);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyBatchAsync(
            [new InstallationStateChange("demo", installation.Id, node.Id, Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None), Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None))]);

        Assert.Empty(admin.Sends);
    }

    [Fact]
    public async Task 报警级别跃迁发一条实例事件且负载为落库值()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-B");
        var installation = NewInstallation(node, "demo", DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.Warning);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().Add(installation);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyAsync(
            "demo",
            installation.Id,
            node.Id,
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.None),
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.Warning));

        Assert.Equal(2, admin.Sends.Count);
        var instance = Assert.IsType<AdminInstallationStateChangedNotification>(admin.Sends[0].Payload);
        Assert.Equal(AdminHubDomains.Devices, admin.Sends[0].Domain);
        Assert.Equal(AdminHubEvents.InstallationStateChanged, admin.Sends[0].EventName);
        Assert.Equal("demo", instance.AppKey);
        Assert.Equal(installation.Id, instance.InstallationId);
        Assert.Equal(node.Id, instance.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Online, instance.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Online, instance.StationStatus);
        Assert.Equal(DeviceAlarmLevels.Warning, instance.AlarmLevel);
        Assert.Equal(clock.UtcNow, instance.OccurredAt);
    }

    [Fact]
    public async Task 实例跃迁但节点聚合不变时不发聚合事件()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-C");
        var alarmCarrier = NewInstallation(node, "demo", DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.Error);
        var changed = NewInstallation(node, "demo", DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().AddRange(alarmCarrier, changed);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyAsync(
            "demo",
            changed.Id,
            node.Id,
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None),
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.Info));

        Assert.Single(admin.Sends);
        Assert.Equal(AdminHubEvents.InstallationStateChanged, admin.Sends[0].EventName);
    }

    [Fact]
    public async Task 节点最后一个在线实例离线时发一条聚合事件()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-D");
        var installation = NewInstallation(node, "demo", DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().Add(installation);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyAsync(
            "demo",
            installation.Id,
            node.Id,
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None),
            Snap(DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None));

        Assert.Equal(2, admin.Sends.Count);
        var nodeEvent = Assert.IsType<AdminNodeStateChangedNotification>(admin.Sends[1].Payload);
        Assert.Equal(AdminHubEvents.NodeStateChanged, admin.Sends[1].EventName);
        Assert.Equal(node.Id, nodeEvent.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Offline, nodeEvent.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Idle, nodeEvent.StationStatus);
        Assert.Equal(DeviceAlarmLevels.None, nodeEvent.AlarmLevel);
    }

    [Fact]
    public async Task 同节点仍有其它在线实例时离线不发聚合事件()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-E");
        var staying = NewInstallation(node, "other", DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        var leaving = NewInstallation(node, "demo", DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().AddRange(staying, leaving);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyAsync(
            "demo",
            leaving.Id,
            node.Id,
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None),
            Snap(DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None));

        Assert.Single(admin.Sends);
        Assert.Equal(AdminHubEvents.InstallationStateChanged, admin.Sends[0].EventName);
    }

    [Fact]
    public async Task 批量离线每实例一条每节点聚合至多一条()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var nodeOne = NewNode("MC-F1");
        var nodeTwo = NewNode("MC-F2");
        var a = NewInstallation(nodeOne, "demo", DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        var b = NewInstallation(nodeOne, "other", DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        var c = NewInstallation(nodeTwo, "demo", DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None);
        db.Set<DeviceNode>().AddRange(nodeOne, nodeTwo);
        db.Set<DeviceInstallation>().AddRange(a, b, c);
        await db.SaveChangesAsync();

        var admin = new RecordingAdminPublisher();
        await CreateNotifier(db, admin, clock).NotifyBatchAsync(
            [
                new InstallationStateChange("demo", a.Id, nodeOne.Id, Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None), Snap(DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None)),
                new InstallationStateChange("other", b.Id, nodeOne.Id, Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None), Snap(DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None)),
                new InstallationStateChange("demo", c.Id, nodeTwo.Id, Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Idle, DeviceAlarmLevels.None), Snap(DeviceOnlineStatuses.Offline, DeviceStationStatuses.Idle, DeviceAlarmLevels.None)),
            ]);

        Assert.Equal(3, admin.Sends.Count(x => x.EventName == AdminHubEvents.InstallationStateChanged));
        Assert.Equal(2, admin.Sends.Count(x => x.EventName == AdminHubEvents.NodeStateChanged));
    }

    [Fact]
    public async Task 发布异常不向调用方传播()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var node = NewNode("MC-G");
        var installation = NewInstallation(node, "demo", DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.Warning);
        db.Set<DeviceNode>().Add(node);
        db.Set<DeviceInstallation>().Add(installation);
        await db.SaveChangesAsync();

        await CreateNotifier(db, new ThrowingAdminPublisher(), clock).NotifyAsync(
            "demo",
            installation.Id,
            node.Id,
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.None),
            Snap(DeviceOnlineStatuses.Online, DeviceStationStatuses.Online, DeviceAlarmLevels.Warning));
    }

    private sealed record AdminSend(string Domain, string EventName, object Payload);

    private sealed class RecordingAdminPublisher : IAdminEventPublisher
    {
        public List<AdminSend> Sends { get; } = [];

        public Task PublishAsync(string domain, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            Sends.Add(new AdminSend(domain, eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAdminPublisher : IAdminEventPublisher
    {
        public Task PublishAsync(string domain, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("域推送失败"));
    }
}
