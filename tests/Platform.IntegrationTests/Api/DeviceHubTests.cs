using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>设备 Hub：鉴权、安装实例注册去重、顶号与心跳。令牌只从查询参数读取，验证客户端真实连接方式。</summary>
public sealed class DeviceHubTests : IClassFixture<PlatformApiFactory>
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    private readonly PlatformApiFactory _factory;

    public DeviceHubTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 令牌连接后可注册心跳且同安装新连接顶替旧连接()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = $"hub-{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAppAsync(client, admin, appKey);
        var token = await DevTokenAsync(client, appKey);

        await using var first = CreateConnection(token);
        await using var second = CreateConnection(token);

        var replaced = new TaskCompletionSource<InstallationSessionReplaced>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.On<InstallationSessionReplaced>(
            DeviceHubEvents.SessionReplaced,
            session => replaced.TrySetResult(session));

        await first.StartAsync();
        var request = new DeviceRegistrationRequest(
            new DeviceIdentity(InstallGuid, "HUB-PRIMARY", null, "AA-BB-CC-DD-EE-FF", "1.0.0"),
            "PC-01",
            "10.1.2.3",
            "full");

        var registered = await first.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation, request);
        Assert.NotEqual(Guid.Empty, registered.InstallationId);
        Assert.NotEqual(Guid.Empty, registered.NodeId);
        Assert.Equal(16, registered.NodeShortId.Length);
        Assert.Equal(16, registered.InstallationShortId.Length);

        var heartbeat = await first.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(
                DeviceHeartbeatStatuses.Online,
                DeviceAlarmLevels.Warning,
                "缺料"));
        Assert.Equal(DeviceOnlineStatuses.Online, heartbeat.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Online, heartbeat.StationStatus);
        Assert.Equal(DeviceAlarmLevels.Warning, heartbeat.AlarmLevel);

        // 同安装再连：返回同一安装/节点 id，旧连接收到 SessionReplaced
        await second.StartAsync();
        var reconnected = await second.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation, request);
        Assert.Equal(registered.InstallationId, reconnected.InstallationId);
        Assert.Equal(registered.NodeId, reconnected.NodeId);

        var session = await replaced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("replaced", session.Reason);

        // 已被顶替的旧连接不能再改状态
        await Assert.ThrowsAnyAsync<Exception>(() => first.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None)));
    }

    [Fact]
    public async Task 同机不同应用共享节点但各自独立注册()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"hub-a-{suffix}";
        var appB = $"hub-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);
        var tokenA = await DevTokenAsync(client, appA);
        var tokenB = await DevTokenAsync(client, appB);

        await using var clientA = CreateConnection(tokenA);
        await using var clientB = CreateConnection(tokenB);
        await clientA.StartAsync();
        await clientB.StartAsync();

        static DeviceRegistrationRequest Request(Guid installGuid, string name) =>
            new(
                new DeviceIdentity(installGuid, "MULTI-APP-PRIMARY", "SYSTEM-DISK", "AA-BB-CC-DD-EE-FF", "1.0.0"),
                name,
                "10.1.2.3",
                "full");

        var installationA = await clientA.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation, Request(Guid.NewGuid(), "PC-app-a"));
        var installationB = await clientB.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation, Request(Guid.NewGuid(), "PC-app-b"));

        // 同一物理机 = 同一节点；应用不同 = 不同安装实例，互不顶号
        Assert.Equal(installationA.NodeId, installationB.NodeId);
        Assert.NotEqual(installationA.InstallationId, installationB.InstallationId);
        Assert.Equal(installationA.MachineCode, installationB.MachineCode);

        var heartbeat = await clientA.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None));
        Assert.Equal(installationA.InstallationId, heartbeat.Id);
        Assert.Equal(DeviceOnlineStatuses.Online, heartbeat.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Idle, heartbeat.StationStatus);
    }

    [Fact]
    public async Task 未认证连接被拒绝且未注册连接不能发心跳()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = $"hub-{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAppAsync(client, admin, appKey);
        var token = await DevTokenAsync(client, appKey);

        await using var anonymous = CreateConnection(token: null);
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());

        await using var connection = CreateConnection(token);
        await connection.StartAsync();
        var heartbeat = connection.InvokeAsync<InstallationSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None));
        await Assert.ThrowsAsync<HubException>(() => heartbeat);
    }

    private HubConnection CreateConnection(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, DeviceHubPaths.Root),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    if (token is not null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    }
                })
            .Build();

    private static async Task CreateAppAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                token,
                new CreateAppRequest(key, key))));

    private static async Task<string> DevTokenAsync(HttpClient client, string appKey)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"hub-{appKey}",
                Name: appKey,
                Permissions: ["*"],
                Apps: [appKey],
                AppKey: appKey));
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<DevTokenResponse>>();
        Assert.True(payload!.IsSuccess, payload.Message);
        return payload.Data!.AccessToken;
    }
}
