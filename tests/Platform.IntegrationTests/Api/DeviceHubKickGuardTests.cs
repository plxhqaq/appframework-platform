using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.Contracts.Identity;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// W2 设备侧实时语义：UserSessionRevoked 定向（跨应用不可见、单会话带 SessionId）、
/// 安装实例/节点停用推事件并踢线、重连注册被拒，以及 QR 审批事件不再跨应用可见。
/// </summary>
public sealed class DeviceHubKickGuardTests : IClassFixture<PlatformApiFactory>
{
    private const string DemoAppKey = "demo";

    private readonly PlatformApiFactory _factory;

    public DeviceHubKickGuardTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 全量会话吊销只定向通知绑定安装实例且旧令牌401()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var guidLogin = Guid.NewGuid();
        var target = await RegisterInstallationAsync(
            await DevTokenAsync(client, DemoAppKey), "KICK-P1-A", guidLogin, $"kick-{guidLogin:N}");
        var other = await ConnectSecondAppInstallationAsync(client, admin, "KICK-P1-B");

        var revoked = new TaskCompletionSource<UserSessionRevokedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        target.Connection.On<UserSessionRevokedNotification>(
            DeviceHubEvents.UserSessionRevoked, x => { revoked.TrySetResult(x); });
        var leaked = 0;
        other.Connection.On<UserSessionRevokedNotification>(
            DeviceHubEvents.UserSessionRevoked, _ => leaked++);

        var login = await LoginWithInstallationAsync(
            client, guidLogin, "p1", target.Registration.MachineCode);
        await RevokeAllAsync(client, admin, login.User.UserId);

        var notification = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DemoAppKey, notification.AppKey);
        Assert.Equal(login.User.UserId, notification.UserId);
        Assert.Equal(guidLogin, notification.InstallGuid);
        Assert.Null(notification.SessionId);
        Assert.Equal("revoked_by_admin", notification.Reason);

        // epoch 兜底：被吊销的登录令牌在所有路径立即 401
        var blocked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/platform/whoami", login.AccessToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(blocked, HttpStatusCode.Unauthorized));

        await Task.Delay(500);
        Assert.Equal(0, leaked);

        await target.Connection.DisposeAsync();
        await other.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 单会话吊销事件携带SessionId且只发目标安装实例()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var guidLogin = Guid.NewGuid();
        var target = await RegisterInstallationAsync(
            await DevTokenAsync(client, DemoAppKey), "KICK-P2-A", guidLogin, $"kick-{guidLogin:N}");
        var other = await ConnectSecondAppInstallationAsync(client, admin, "KICK-P2-B");

        var revoked = new TaskCompletionSource<UserSessionRevokedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        target.Connection.On<UserSessionRevokedNotification>(
            DeviceHubEvents.UserSessionRevoked, x => { revoked.TrySetResult(x); });
        var leaked = 0;
        other.Connection.On<UserSessionRevokedNotification>(
            DeviceHubEvents.UserSessionRevoked, _ => leaked++);

        var victimFingerprint = $"p2-{Guid.NewGuid():N}";
        var first = await LoginWithInstallationAsync(
            client, guidLogin, victimFingerprint, target.Registration.MachineCode);
        await LoginWithInstallationAsync(
            client, guidLogin, $"{victimFingerprint}-other", target.Registration.MachineCode);

        var sessions = await IdentityTestHelpers.ReadDataAsync<PageResult<SessionSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/identity/sessions?userId={first.User.UserId:D}&pageSize=100",
                admin)));
        var victim = Assert.Single(
            sessions.Items,
            x => x.IsActive && string.Equals(x.DeviceFingerprint, victimFingerprint, StringComparison.Ordinal));

        await IdentityTestHelpers.ReadDataAsync<SessionSummaryDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/sessions/{victim.Id}/revoke",
                admin)));

        var notification = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(victim.Id, notification.SessionId);
        Assert.Equal(guidLogin, notification.InstallGuid);

        await Task.Delay(500);
        Assert.Equal(0, leaked);

        await target.Connection.DisposeAsync();
        await other.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 停用安装实例推送事件下次上行即断线且重连注册被拒()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var installGuid = Guid.NewGuid();
        var serial = $"kick-{installGuid:N}";
        var installation = await RegisterInstallationAsync(
            await DevTokenAsync(client, DemoAppKey), "KICK-P3-A", installGuid, serial);

        var disabled = new TaskCompletionSource<InstallationDisabledNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installation.Connection.On<InstallationDisabledNotification>(
            DeviceHubEvents.InstallationDisabled, x => { disabled.TrySetResult(x); });
        installation.Connection.Closed += _ => Task.FromResult(closed.TrySetResult());

        // 管理端停用：先推 InstallationDisabled，再打待踢标记
        var disabledDetail = await IdentityTestHelpers.ReadDataAsync<InstallationDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/installations/{installation.Registration.InstallationId}",
                admin,
                new UpdateInstallationRequest(IsEnabled: false))));

        Assert.False(disabledDetail.IsEnabled);
        var notification = await disabled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(installation.Registration.InstallationId, notification.InstallationId);

        // 客户端下次上行即被守卫断线（验收口径 ≤10s，无需等空闲踢线扫描）
        await TrySendHeartbeatAsync(installation.Connection);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 重连被注册路径拒绝（806）：新连接能建立，但注册调用抛 HubException
        var reconnect = await ConnectAsync(await DevTokenAsync(client, DemoAppKey));
        await reconnect.StartAsync();
        // SignalR 只把 HubException 的原文透传给客户端，业务异常在服务端记录、
        // 客户端收到通用失败——806/807 具体错误码语义由 PostgresTests 在服务层断言。
        await Assert.ThrowsAsync<HubException>(() => reconnect.InvokeAsync(
            DeviceHubMethods.RegisterInstallation,
            CreateRegistrationRequest(installGuid, serial, "KICK-P3-RECONNECT")));
        await reconnect.DisposeAsync();
        await installation.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 停用节点同机全部应用连接收到事件并被踢线()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var guidA = Guid.NewGuid();
        var guidB = Guid.NewGuid();
        var serial = $"kick-node-{guidA:N}";

        var connectionA = await RegisterInstallationAsync(
            await DevTokenAsync(client, DemoAppKey), "KICK-P4-A", guidA, serial);
        var connectionB = await ConnectInstallationAsync(
            client, admin, "KICK-P4-B", guidB, serial);

        var nodeId = connectionA.Registration.NodeId;
        var nodeDisabledA = new TaskCompletionSource<NodeDisabledNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nodeDisabledB = new TaskCompletionSource<NodeDisabledNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionA.Connection.On<NodeDisabledNotification>(
            DeviceHubEvents.NodeDisabled, x => { nodeDisabledA.TrySetResult(x); });
        connectionB.Connection.On<NodeDisabledNotification>(
            DeviceHubEvents.NodeDisabled, x => { nodeDisabledB.TrySetResult(x); });
        connectionA.Connection.Closed += _ => Task.FromResult(closedA.TrySetResult());
        connectionB.Connection.Closed += _ => Task.FromResult(closedB.TrySetResult());

        await IdentityTestHelpers.ReadDataAsync<NodeDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/nodes/{nodeId}",
                admin,
                new UpdateNodeRequest(IsEnabled: false))));

        Assert.Equal(nodeId, (await nodeDisabledA.Task.WaitAsync(TimeSpan.FromSeconds(10))).NodeId);
        Assert.Equal(nodeId, (await nodeDisabledB.Task.WaitAsync(TimeSpan.FromSeconds(10))).NodeId);

        await TrySendHeartbeatAsync(connectionA.Connection);
        await TrySendHeartbeatAsync(connectionB.Connection);
        await closedA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await closedB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await connectionA.Connection.DisposeAsync();
        await connectionB.Connection.DisposeAsync();
    }

    [Fact]
    public async Task QR审批事件只达发起安装实例跨应用不可见()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var guidLogin = Guid.NewGuid();
        var serial = $"kick-{guidLogin:N}";
        var target = await RegisterInstallationAsync(
            await DevTokenAsync(client, DemoAppKey), "KICK-P5-A", guidLogin, serial);
        var other = await ConnectInstallationAsync(client, admin, "KICK-P5-B", Guid.NewGuid(), serial);

        var approvedEvent = new TaskCompletionSource<QrLoginApprovedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        target.Connection.On<QrLoginApprovedNotification>(
            DeviceHubEvents.QrLoginApproved, x => { approvedEvent.TrySetResult(x); });
        var leaked = 0;
        other.Connection.On<QrLoginApprovedNotification>(
            DeviceHubEvents.QrLoginApproved, _ => leaked++);

        var created = await IdentityTestHelpers.ReadDataAsync<QrSessionCreatedDto>(
            await client.PostAsJsonAsync(
                "/api/v1/identity/qr/sessions",
                new CreateQrSessionRequest(
                    AppKey: DemoAppKey,
                    DeviceFingerprint: $"p5-{Guid.NewGuid():N}",
                    ClientName: "KICK-P5",
                    MachineCode: target.Registration.MachineCode,
                    InstallGuid: guidLogin)));

        await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/qr/sessions/{created.RequestId}/approve",
                admin,
                new ApproveQrSessionRequest())));

        var notification = await approvedEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(created.RequestId, notification.RequestId);
        Assert.True(notification.Approved);

        await Task.Delay(500);
        Assert.Equal(0, leaked);

        await target.Connection.DisposeAsync();
        await other.Connection.DisposeAsync();
    }

    private async Task<HubConnection> ConnectAsync(string token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, DeviceHubPaths.Root),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();

    private static DeviceRegistrationRequest CreateRegistrationRequest(
        Guid installGuid,
        string serial,
        string displayName) =>
        new(
            new DeviceIdentity(
                InstallGuid: installGuid,
                SmbiosUuid: null,
                SystemDiskSerial: serial,
                FrameworkVersion: "1.0.0"),
            displayName,
            IpAddress: "10.9.8.7");

    private async Task<TestInstallation> RegisterInstallationAsync(
        string token,
        string displayName,
        Guid installGuid,
        string serial)
    {
        var connection = await ConnectAsync(token);
        await connection.StartAsync();
        var registration = await connection.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation,
            CreateRegistrationRequest(installGuid, serial, displayName));
        return new TestInstallation(connection, registration);
    }

    /// <summary>第二个应用（每次调用新建 app）的安装实例连接。</summary>
    private async Task<TestInstallation> ConnectSecondAppInstallationAsync(
        HttpClient client,
        string adminToken,
        string displayName)
    {
        var installGuid = Guid.NewGuid();
        return await ConnectInstallationAsync(
            client, adminToken, displayName, installGuid, $"kick-{installGuid:N}");
    }

    private async Task<TestInstallation> ConnectInstallationAsync(
        HttpClient client,
        string adminToken,
        string displayName,
        Guid installGuid,
        string serial)
    {
        var suffix = installGuid.ToString("N")[..12];
        var appKey = $"kick-iso-{suffix}";
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(appKey, "守卫隔离测试"))));

        return await RegisterInstallationAsync(
            await DevTokenAsync(client, appKey), displayName, installGuid, serial);
    }

    private static async Task<string> DevTokenAsync(HttpClient client, string appKey)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"kick-guard-{Guid.NewGuid():N}",
                Name: appKey,
                Apps: [appKey],
                AppKey: appKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }

    /// <summary>以安装实例身份（demo 上下文 + installGuid 绑定）登录管理员，返回桌面端登录结果。</summary>
    private static async Task<LoginResponse> LoginWithInstallationAsync(
        HttpClient client,
        Guid installGuid,
        string fingerprint,
        string machineCode)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new LoginRequest(
                IdentityTestHelpers.AdminUserName,
                IdentityTestHelpers.AdminPassword,
                DemoAppKey,
                DeviceFingerprint: fingerprint,
                machineCode,
                InstallGuid: installGuid));
        return await IdentityTestHelpers.ReadDataAsync<LoginResponse>(response);
    }

    private static async Task RevokeAllAsync(HttpClient client, string adminToken, Guid userId) =>
        await IdentityTestHelpers.ReadDataAsync<RevokedSessionsResponse>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/identity/users/{userId:D}/sessions/revoke",
                adminToken)));

    private static async Task TrySendHeartbeatAsync(HubConnection connection)
    {
        try
        {
            await connection.InvokeAsync(
                DeviceHubMethods.Heartbeat,
                new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
        }
        catch (Exception)
        {
            // 断线竞态下上行调用可能本地抛错；只要连接进入 Closed 即证明守卫踢线生效
        }
    }

    private sealed record TestInstallation(HubConnection Connection, DeviceRegistrationResponse Registration);

    private sealed record RevokedSessionsResponse(int RevokedSessions);
}
