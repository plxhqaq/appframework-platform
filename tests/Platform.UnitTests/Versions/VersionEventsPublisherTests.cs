using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Kernel.Abstractions;
using Platform.Modules.Versions.Services;

namespace Platform.UnitTests.Versions;

/// <summary>
/// W3 广播三件套的发送方语义：<see cref="VersionEventsPublisher"/> 路由事件名/负载正确，
/// Devices 模块缺席静默跳过，推送异常只吞不抛（推送只是提示，不得影响落库结果）。
/// </summary>
public sealed class VersionEventsPublisherTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 发布提示走应用组且负载携带渠道与版本()
    {
        var router = new RecordingRouter();
        var publisher = CreatePublisher(router, new RecordingAdminPublisher());

        await publisher.ReleasePublishedAsync("demo", "stable", "1.2.3");

        Assert.Single(router.Sends);
        var (appKey, eventName, payload) = router.Sends[0];
        Assert.Equal("demo", appKey);
        Assert.Equal(DeviceHubEvents.ReleasePublished, eventName);
        Assert.Equal(new ReleasePublishedNotification("demo", "stable", "1.2.3"), payload);
    }

    [Fact]
    public async Task 路由缺席时发布提示静默跳过()
    {
        var publisher = CreatePublisher(null, new RecordingAdminPublisher());

        await publisher.ReleasePublishedAsync("demo", "stable", "1.2.3");
    }

    [Fact]
    public async Task 路由抛错不向调用方传播()
    {
        var publisher = CreatePublisher(new ThrowingRouter(), new RecordingAdminPublisher());

        await publisher.ReleasePublishedAsync("demo", "stable", "1.2.3");
    }

    [Fact]
    public async Task 摄取进度走versions域并带服务端时间()
    {
        var router = new RecordingRouter();
        var admin = new RecordingAdminPublisher();
        var publisher = CreatePublisher(router, admin);
        var packageId = Guid.NewGuid();

        await publisher.IngestionProgressAsync("demo", packageId, "processing", 0.0);
        await publisher.IngestionProgressAsync("demo", packageId, "ready", 1.0);

        Assert.Equal(2, admin.Sends.Count);
        Assert.Equal(AdminHubDomains.Versions, admin.Sends[0].Domain);
        Assert.Equal(AdminHubEvents.ReleaseIngestionProgress, admin.Sends[0].EventName);
        var ready = Assert.IsType<AdminReleaseIngestionProgressNotification>(admin.Sends[1].Payload);
        Assert.Equal(packageId, ready.PackageId);
        Assert.Equal("ready", ready.Status);
        Assert.Equal(1.0, ready.Progress);
        Assert.Equal(FixedNow, ready.OccurredAt);
    }

    [Fact]
    public async Task 管理台推送抛错不向调用方传播()
    {
        var publisher = CreatePublisher(new RecordingRouter(), new ThrowingAdminPublisher());

        await publisher.IngestionProgressAsync("demo", Guid.NewGuid(), "failed", 0.0);
    }

    private static VersionEventsPublisher CreatePublisher(
        IInstallationEventRouter? router,
        IAdminEventPublisher adminEvents) =>
        new(
            router is null ? [] : [router],
            adminEvents,
            new FixedClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VersionEventsPublisher>.Instance);

    private sealed record Send(string AppKey, string EventName, object Payload);

    private sealed class RecordingRouter : IInstallationEventRouter
    {
        public List<Send> Sends { get; } = [];

        public Task ToInstallationAsync(string appKey, Guid installationId, string eventName, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ToMachineAsync(string appKey, string machineCode, string eventName, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ToInstallGuidAsync(string appKey, Guid installGuid, string eventName, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ToAppAsync(string appKey, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            Sends.Add(new Send(appKey, eventName, payload));
            return Task.CompletedTask;
        }

        public Task ToNodeAsync(Guid nodeId, string eventName, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int AppConnectionCount(string appKey) => 0;
    }

    private sealed class ThrowingRouter : IInstallationEventRouter
    {
        public Task ToInstallationAsync(string appKey, Guid installationId, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ToMachineAsync(string appKey, string machineCode, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ToInstallGuidAsync(string appKey, Guid installGuid, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ToAppAsync(string appKey, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("广播失败"));

        public Task ToNodeAsync(Guid nodeId, string eventName, object payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public int AppConnectionCount(string appKey) => 0;
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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => FixedNow;
    }
}
