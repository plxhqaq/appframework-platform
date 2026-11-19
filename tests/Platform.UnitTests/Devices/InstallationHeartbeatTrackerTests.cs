using Microsoft.Extensions.Options;
using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Options;
using Platform.Modules.Devices.Services;

namespace Platform.UnitTests.Devices;

/// <summary>心跳内存态登记表：命中/失效、脏标记版本与 TTL 过期语义。</summary>
public sealed class InstallationHeartbeatTrackerTests
{
    private static readonly Guid NodeId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static InstallationSummaryDto Summary(Guid installationId, string online) => new(
        installationId, NodeId, "PC-01", "工位1", null, null, "app-a", "MACHINE-1", "shortid12345678",
        Guid.NewGuid(), "full", "1.0.0", online, DeviceStationStatuses.Online, DeviceAlarmLevels.None,
        null, null, null, 10, true, true);

    private static (InstallationHeartbeatTracker Tracker, MutableClock Clock) Create(
        int ttlSeconds = 0)
    {
        var clock = new MutableClock();
        var options = Options.Create(new DevicesOptions { HeartbeatCacheTtlSeconds = ttlSeconds });
        return (new InstallationHeartbeatTracker(clock, options), clock);
    }

    [Fact]
    public void Seed后同应用命中且异应用不误命中()
    {
        var installationId = Guid.NewGuid();
        var (tracker, clock) = Create();
        tracker.Seed("app-a", installationId, NodeId, Summary(installationId, DeviceOnlineStatuses.Online), clock.UtcNow, timestampPersisted: true);

        Assert.True(tracker.TryGet("app-a", installationId, out var entry));
        Assert.Equal(installationId, entry.Summary.Id);

        // 应用键不匹配视为未命中，并顺手清理错配条目（防连接迁移后的陈旧归属）。
        Assert.False(tracker.TryGet("app-b", installationId, out _));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void 脏标记按批次版本回收且写穿条目不入批()
    {
        var installationId = Guid.NewGuid();
        var (tracker, clock) = Create();
        tracker.Seed("app-a", installationId, NodeId, Summary(installationId, DeviceOnlineStatuses.Online), clock.UtcNow, timestampPersisted: true);
        Assert.Empty(tracker.SnapshotDirty());

        // 纯心跳：脏但未写库，进入回写批次。
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        tracker.Touch(installationId, Summary(installationId, DeviceOnlineStatuses.Online), clock.UtcNow, timestampPersisted: false);
        var batch = tracker.SnapshotDirty();
        var dirty = Assert.Single(batch);
        Assert.Equal(installationId, dirty.InstallationId);
        Assert.Equal(clock.UtcNow, dirty.LastHeartbeatAt);

        tracker.MarkFlushed(batch);
        Assert.Empty(tracker.SnapshotDirty());

        // 回写后再次脏化，但本批以写穿收尾：不应滞留脏标记。
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        tracker.Touch(installationId, Summary(installationId, DeviceStationStatuses.Idle), clock.UtcNow, timestampPersisted: true);
        Assert.Empty(tracker.SnapshotDirty());
    }

    [Fact]
    public void TTL过期条目按未命中处理并可清理()
    {
        var (tracker, clock) = Create(ttlSeconds: 60);
        var accessedId = Guid.NewGuid();
        var leakedId = Guid.NewGuid();
        tracker.Seed("app-a", accessedId, NodeId, Summary(accessedId, DeviceOnlineStatuses.Online), clock.UtcNow, timestampPersisted: false);
        tracker.Seed("app-a", leakedId, NodeId, Summary(leakedId, DeviceOnlineStatuses.Online), clock.UtcNow, timestampPersisted: false);

        clock.UtcNow = clock.UtcNow.AddSeconds(59);
        Assert.True(tracker.TryGet("app-a", accessedId, out _));

        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        Assert.False(tracker.TryGet("app-a", accessedId, out _));

        // accessedId 已被未命中路径顺带清理，SweepExpired 收走无人访问的滞留条目。
        Assert.Equal(1, tracker.SweepExpired());
        Assert.Equal(0, tracker.Count);
    }
}
