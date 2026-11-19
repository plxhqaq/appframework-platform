using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Services;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// W4 管理台状态事件集成：安装实例/节点跃迁只提示一次、相同心跳静默、
/// 重连/断开/离线扫描三条路径都有事件且负载为落库值；
/// 扫码会话创建与取消推送 QrSessionChanged；devices 域外连接收不到状态事件。
/// 验收口径：事件只是提示，字段必须能被管理台用来回拉对账。
/// </summary>
public sealed class AdminHubStateEventsTests : IClassFixture<PlatformApiFactory>
{
    private const string DemoAppKey = "demo";

    private readonly PlatformApiFactory _factory;

    public AdminHubStateEventsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 心跳状态跃迁推送一次且相同心跳静默()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var (installs, nodes) = await OpenAdminHubAsync(client, admin);
        await using var device = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-HB-A");

        // 注册即上线是新行首帧，不发提示；先确认基线静默。
        await Task.Delay(300);
        Assert.Empty(installs);
        Assert.Empty(nodes);

        // 与首帧三值一致的空闲心跳：无跃迁，必须完全静默（心跳是高频路径）。
        await device.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None));
        await device.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None));
        await Task.Delay(300);
        Assert.Empty(installs);
        Assert.Empty(nodes);

        // 工位 idle → online：实例与节点聚合各推一条，负载为落库值。
        await device.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
        await WaitUntilAsync(() => !installs.IsEmpty, "工位跃迁未推送安装实例事件");
        var flipped = DequeueOne(installs);
        Assert.Equal(DemoAppKey, flipped.AppKey);
        Assert.NotEqual(Guid.Empty, flipped.InstallationId);
        Assert.NotEqual(Guid.Empty, flipped.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Online, flipped.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Online, flipped.StationStatus);
        Assert.Equal(DeviceAlarmLevels.None, flipped.AlarmLevel);
        Assert.True(flipped.OccurredAt != default);

        await WaitUntilAsync(() => !nodes.IsEmpty, "工位跃迁未推送节点聚合事件");
        var node = DequeueOne(nodes);
        Assert.Equal(flipped.NodeId, node.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Online, node.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Online, node.StationStatus);

        // 相同心跳重复：不得再发。
        await device.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
        await Task.Delay(300);
        Assert.True(installs.IsEmpty);
        Assert.True(nodes.IsEmpty);
    }

    [Fact]
    public async Task 已离线实例重新注册即上线并推送事件()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var (installs, nodes) = await OpenAdminHubAsync(client, admin);
        var installGuid = Guid.NewGuid();

        await using (var first = await ConnectInstallationAsync(
            client, admin, DemoAppKey, "STATE-RC-A", installGuid))
        {
            await first.InvokeAsync<InstallationSummaryDto>(
                DeviceHubMethods.Heartbeat,
                new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Offline, DeviceAlarmLevels.None));
            await WaitUntilAsync(
                () => !installs.IsEmpty && !nodes.IsEmpty,
                "离线跃迁未推事实例与节点聚合事件");
        }

        // 排空下线阶段事件，之后只观察"注册即上线"这一条路径。
        Drain(installs);
        Drain(nodes);
        await Task.Delay(300);
        Drain(installs);
        Drain(nodes);

        await using (var reconnected = await ConnectInstallationAsync(
            client, admin, DemoAppKey, "STATE-RC-A", installGuid))
        {
            await WaitUntilAsync(() => !installs.IsEmpty, "重连注册未推送上线事件");
            var online = DequeueOne(installs);
            Assert.Equal(reconnected.Registration.InstallationId, online.InstallationId);
            Assert.Equal(reconnected.Registration.NodeId, online.NodeId);
            Assert.Equal(DeviceOnlineStatuses.Online, online.OnlineStatus);
        }
    }

    [Fact]
    public async Task 连接断开立即离线并推送实例与节点聚合事件()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var (installs, nodes) = await OpenAdminHubAsync(client, admin);
        var device = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-DC-A");
        await Task.Delay(300);
        Drain(installs);
        Drain(nodes);

        // 正常退出不等心跳超时：断开即离线，节点唯一实例下线即聚合翻转为离线。
        await device.DisposeAsync();

        await WaitUntilAsync(() => !installs.IsEmpty, "断开未推送实例离线事件");
        var offline = DequeueOne(installs);
        Assert.Equal(device.Registration.InstallationId, offline.InstallationId);
        Assert.Equal(device.Registration.NodeId, offline.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Offline, offline.OnlineStatus);

        await WaitUntilAsync(() => !nodes.IsEmpty, "断开未推送节点聚合离线事件");
        var node = DequeueOne(nodes);
        Assert.Equal(device.Registration.NodeId, node.NodeId);
        Assert.Equal(DeviceOnlineStatuses.Offline, node.OnlineStatus);
    }

    [Fact]
    public async Task 同节点另一实例仍在线时不发节点聚合事件()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var (installs, nodes) = await OpenAdminHubAsync(client, admin);
        var machine = $"st-{Guid.NewGuid():N}";

        // 同机同应用重装语义：第二路注册顶掉第一路连接。被顶替连接已无活动登记，
        // 断开不再离线，其安装实例行保持在线——正好充当"节点上另一路在线实例"。
        var first = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-SH-A", machineCode: machine);
        var second = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-SH-B", machineCode: machine);
        Assert.Equal(first.Registration.NodeId, second.Registration.NodeId);
        await Task.Delay(300);
        Drain(installs);
        Drain(nodes);

        // 断开持有连接的实例：实例事件照发，但节点仍有另一路在线实例，聚合不得翻转。
        await second.DisposeAsync();
        await WaitUntilAsync(
            () => installs.Any(x => x.InstallationId == second.Registration.InstallationId),
            "断开实例未推送实例离线事件");
        await Task.Delay(500);
        Assert.True(nodes.IsEmpty);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task 离线扫描按实例与节点聚合各推一次()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var (installs, nodes) = await OpenAdminHubAsync(client, admin);
        await using var a = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-OF-A");
        await using var b = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-OF-B");
        await Task.Delay(300);
        Drain(installs);
        Drain(nodes);

        // 把心跳陈旧化后触发扫描：两个节点各一条实例事件 + 各一条聚合事件。
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ids = new[] { a.Registration.InstallationId, b.Registration.InstallationId };
            var stale = DateTimeOffset.UtcNow.AddHours(-2);
            var rows = await db.Set<DeviceInstallation>()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
            {
                row.LastHeartbeatAt = stale;
            }

            await db.SaveChangesAsync();

            var presence = scope.ServiceProvider.GetRequiredService<IInstallationPresence>();
            var marked = await presence.MarkOfflineAsync();
            Assert.True(marked >= 2);
        }

        await WaitUntilAsync(
            () => installs.Count(x => x.OnlineStatus == DeviceOnlineStatuses.Offline) >= 2,
            "离线扫描未逐实例推送事件");
        Assert.Contains(installs, x => x.InstallationId == a.Registration.InstallationId);
        Assert.Contains(installs, x => x.InstallationId == b.Registration.InstallationId);
        await WaitUntilAsync(() => nodes.Count >= 2, "离线扫描未逐节点推送聚合事件");
        Assert.Contains(nodes, x => x.NodeId == a.Registration.NodeId);
        Assert.Contains(nodes, x => x.NodeId == b.Registration.NodeId);
    }

    [Fact]
    public async Task 扫码会话创建与取消推送会话变化事件()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var qrEvents = new ConcurrentQueue<AdminQrSessionChangedNotification>();
        await using var adminHub = await ConnectAsync(AdminHubPaths.Root, admin);
        adminHub.On<AdminQrSessionChangedNotification>(AdminHubEvents.QrSessionChanged, x => qrEvents.Enqueue(x));
        await adminHub.StartAsync();

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(
                "/api/v1/identity/qr/sessions",
                new CreateQrSessionRequest(AppKey: DemoAppKey, ClientName: "state-qr")));

        await WaitUntilAsync(() => !qrEvents.IsEmpty, "扫码会话创建未推送事件");
        var pending = DequeueOne(qrEvents);
        Assert.Equal(DemoAppKey, pending.AppKey);
        Assert.Equal(created.RequestId, pending.SessionId);
        Assert.Equal(QrLoginStatuses.Pending, pending.Status);

        var cancelled = await client.PostAsync(
            $"/api/v1/identity/qr/sessions/{created.RequestId}/cancel", content: null);
        Assert.True(cancelled.IsSuccessStatusCode);

        await WaitUntilAsync(() => !qrEvents.IsEmpty, "扫码会话取消未推送事件");
        var done = DequeueOne(qrEvents);
        Assert.Equal(created.RequestId, done.SessionId);
        Assert.Equal(QrLoginStatuses.Cancelled, done.Status);
    }

    [Fact]
    public async Task 无devices域权限的连接收不到状态事件()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var limitedToken = await DevTokenAsync(client, DemoAppKey, ["versions.read"]);
        var leaked = new ConcurrentQueue<AdminInstallationStateChangedNotification>();
        await using var limitedHub = await ConnectAsync(AdminHubPaths.Root, limitedToken);
        limitedHub.On<AdminInstallationStateChangedNotification>(
            AdminHubEvents.InstallationStateChanged, x => leaked.Enqueue(x));
        await limitedHub.StartAsync();

        // 阳性对照：管理员连接必须收到同一次跃迁的事件，否则是探针没通而不是隔离生效。
        var (installs, _) = await OpenAdminHubAsync(client, admin);
        await using var device = await ConnectInstallationAsync(client, admin, DemoAppKey, "STATE-ISO-A");
        await device.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
        await WaitUntilAsync(() => !installs.IsEmpty, "阳性对照失败：管理员连接也没收到状态事件");

        await Task.Delay(500);
        Assert.True(leaked.IsEmpty);
    }

    private static void Drain<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _))
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failMessage)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(failMessage);
    }

    private static T DequeueOne<T>(ConcurrentQueue<T> queue)
    {
        Assert.True(queue.TryDequeue(out var item));
        return item;
    }

    /// <summary>连接管理台 Hub 并同时挂上两类设备状态事件队列（管理员通配权限进全部域组）。</summary>
    private async Task<(ConcurrentQueue<AdminInstallationStateChangedNotification>, ConcurrentQueue<AdminNodeStateChangedNotification>)>
        OpenAdminHubAsync(HttpClient client, string adminToken)
    {
        var installs = new ConcurrentQueue<AdminInstallationStateChangedNotification>();
        var nodes = new ConcurrentQueue<AdminNodeStateChangedNotification>();
        var hub = await ConnectAsync(AdminHubPaths.Root, adminToken);
        hub.On<AdminInstallationStateChangedNotification>(AdminHubEvents.InstallationStateChanged, x => installs.Enqueue(x));
        hub.On<AdminNodeStateChangedNotification>(AdminHubEvents.NodeStateChanged, x => nodes.Enqueue(x));
        await hub.StartAsync();
        return (installs, nodes);
    }

    private async Task<HubConnection> ConnectAsync(string hubPath, string token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, hubPath),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();

    private sealed record TestInstallation(DeviceRegistrationResponse Registration, HubConnection Connection)
    {
        public Task<T> InvokeAsync<T>(string method, object argument) =>
            Connection.InvokeAsync<T>(method, argument);

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private async Task<TestInstallation> ConnectInstallationAsync(
        HttpClient client,
        string adminToken,
        string appKey,
        string displayName,
        Guid? installGuid = null,
        string? machineCode = null)
    {
        var connection = await ConnectAsync(DeviceHubPaths.Root, await DevTokenAsync(client, appKey));
        await connection.StartAsync();
        var guid = installGuid ?? Guid.NewGuid();
        // 同一 machineCode（主标识）+ 同一 appKey 落同一节点；installGuid 区分安装实例。
        var machine = machineCode ?? $"st-{guid:N}";
        var registration = await connection.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation,
            new DeviceRegistrationRequest(
                new DeviceIdentity(
                    InstallGuid: guid,
                    SmbiosUuid: null,
                    SystemDiskSerial: machine,
                    FrameworkVersion: "1.0.0"),
                displayName,
                IpAddress: "10.9.8.7"));
        return new TestInstallation(registration, connection);
    }

    private static async Task<string> DevTokenAsync(
        HttpClient client,
        string appKey,
        string[]? permissions = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"state-{Guid.NewGuid():N}",
                Name: appKey,
                Permissions: permissions,
                Apps: [appKey],
                AppKey: appKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }
}
