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

/// <summary>设备 Hub：鉴权、注册去重、顶号与心跳。令牌只从查询参数读取，验证客户端真实连接方式。</summary>
public sealed class DeviceHubTests : IClassFixture<PlatformApiFactory>
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    private readonly PlatformApiFactory _factory;

    public DeviceHubTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 令牌连接后可注册心跳且同设备新连接顶替旧连接()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = $"hub-{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAppAsync(client, admin, appKey);
        var token = await DevTokenAsync(client, appKey);

        await using var first = CreateConnection(token);
        await using var second = CreateConnection(token);

        var replaced = new TaskCompletionSource<DeviceSessionReplaced>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.On<DeviceSessionReplaced>(
            DeviceHubEvents.SessionReplaced,
            session => replaced.TrySetResult(session));

        await first.StartAsync();
        var request = new DeviceRegistrationRequest(
            new DeviceIdentity("AA-BB-CC-DD-EE-FF", InstallGuid, "1.0.0", "HUB-PRIMARY"),
            "PC-01",
            "10.1.2.3",
            "full");

        var registered = await first.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterDevice, request);
        Assert.NotEqual(Guid.Empty, registered.DeviceId);
        Assert.Equal(16, registered.DeviceShortId.Length);

        var heartbeat = await first.InvokeAsync<DeviceSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(
                DeviceHeartbeatStatuses.Online,
                DeviceAlarmLevels.Warning,
                "缺料"));
        Assert.Equal(DeviceOnlineStatuses.Online, heartbeat.OnlineStatus);
        Assert.Equal(DeviceStationStatuses.Online, heartbeat.StationStatus);
        Assert.Equal(DeviceAlarmLevels.Warning, heartbeat.AlarmLevel);

        // 同身份再连：返回同一设备 id，旧连接收到 SessionReplaced
        await second.StartAsync();
        var reconnected = await second.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterDevice, request);
        Assert.Equal(registered.DeviceId, reconnected.DeviceId);

        var session = await replaced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("replaced", session.Reason);

        // 已被顶替的旧连接不能再改状态
        await Assert.ThrowsAnyAsync<Exception>(() => first.InvokeAsync<DeviceSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None)));
    }

    [Fact]
    public async Task 同机不同安装的连接可并存且共享同一条设备()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = $"hub-{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAppAsync(client, admin, appKey);
        var token = await DevTokenAsync(client, appKey);

        await using var fullClient = CreateConnection(token);
        await using var toolClient = CreateConnection(token);
        await fullClient.StartAsync();
        await toolClient.StartAsync();

        static DeviceRegistrationRequest Request(Guid installGuid, string name) =>
            new(
                new DeviceIdentity("AA-BB-CC-DD-EE-FF", installGuid, "1.0.0", "MULTI-INSTALL-PRIMARY"),
                name,
                "10.1.2.3",
                "full");

        var full = await fullClient.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterDevice, Request(Guid.NewGuid(), "PC-full"));
        var tool = await toolClient.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterDevice, Request(Guid.NewGuid(), "PC-tool"));

        Assert.Equal(full.DeviceId, tool.DeviceId);
        Assert.Equal(full.DeviceShortId, tool.DeviceShortId);

        // 不同安装不会互相顶号：先注册的连接仍可继续心跳。
        var heartbeat = await fullClient.InvokeAsync<DeviceSummaryDto>(
            DeviceHubMethods.Heartbeat,
            new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None));
        Assert.Equal(full.DeviceId, heartbeat.Id);
        Assert.Equal(DeviceOnlineStatuses.Online, heartbeat.OnlineStatus);
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
        var heartbeat = connection.InvokeAsync<DeviceSummaryDto>(
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
