using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Devices;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.Kernel.Abstractions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 设备定向下发：在线/待机门禁、通用命令形状、Ack 成功/失败/超时、管理端重启。
/// 设备用真实 SignalR 客户端连接，覆盖 Hub 与命令网关的完整链路。
/// </summary>
public sealed class DevicePushTests : IClassFixture<PlatformApiFactory>
{
    private const string DemoAppKey = "demo";

    private readonly PlatformApiFactory _factory;

    public DevicePushTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 在线待机设备收到通用命令并可回执成功()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var backup = await CreateBackupAsync(client, admin);
        var token = await DevTokenAsync(client, [PermissionCatalog.BackupPush]);

        var device = await RegisterDeviceAsync(token, "PUSH-OK");
        await using var connection = device.Connection;
        var received = new TaskCompletionSource<DeviceConfigPush>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<DeviceConfigPush>(
            DeviceHubEvents.ConfigPush,
            async command =>
            {
                received.TrySetResult(command);
                await connection.InvokeAsync(
                    DeviceHubMethods.AcknowledgeConfigPush,
                    new DeviceConfigPushAck(command.BackupId, true, "applied"));
            });

        var result = await PushAsync(client, admin, backup.Id, device.DeviceId, restartAfterApply: true);

        var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(backup.Id, command.BackupId);
        Assert.Equal(BackupConventions.KindConfig, command.Kind);
        Assert.Null(command.PayloadJson);
        Assert.Equal($"/api/v1/client/backups/{backup.Id:D}/download", command.DownloadUrl);
        Assert.Equal(backup.Sha256, command.Sha256);
        Assert.True(command.RestartAfterApply);
        Assert.Equal(BackupConventions.DefaultChannel, command.Branch);

        Assert.True(result.Acked);
        Assert.True(result.Success);
        Assert.Equal("applied", result.Message);
    }

    [Fact]
    public async Task 目标留空被拒绝且多设备逐台汇总回执()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var backup = await CreateBackupAsync(client, admin);
        var token = await DevTokenAsync(client, [PermissionCatalog.BackupPush]);

        var blank = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            admin,
            new PushBackupRequest(null, null, "1.0.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(blank, HttpStatusCode.BadRequest));

        var applied = await RegisterDeviceAsync(token, "PUSH-MULTI-OK");
        await using var appliedConnection = applied.Connection;
        applied.Connection.On<DeviceConfigPush>(
            DeviceHubEvents.ConfigPush,
            async command => await applied.Connection.InvokeAsync(
                DeviceHubMethods.AcknowledgeConfigPush,
                new DeviceConfigPushAck(command.BackupId, true, "applied")));

        var failed = await RegisterDeviceAsync(token, "PUSH-MULTI-FAIL");
        await using var failedConnection = failed.Connection;
        failed.Connection.On<DeviceConfigPush>(
            DeviceHubEvents.ConfigPush,
            async command => await failed.Connection.InvokeAsync(
                DeviceHubMethods.AcknowledgeConfigPush,
                new DeviceConfigPushAck(command.BackupId, false, "disk-full")));

        var result = await IdentityTestHelpers.ReadDataAsync<PushBackupResult>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                admin,
                new PushBackupRequest(
                    null, null, "1.0.0", false, false, null, false,
                    TargetDeviceIds: [applied.DeviceId, failed.DeviceId]))));

        Assert.True(result.Acked);
        Assert.False(result.Success);
        Assert.NotNull(result.Targets);
        Assert.Equal(2, result.Targets!.Count);
        Assert.Contains(
            result.Targets,
            x => x.DeviceId == applied.DeviceId && x.Delivered && x.Acked && x.Success);
        Assert.Contains(
            result.Targets,
            x => x.DeviceId == failed.DeviceId && x.Delivered && x.Acked && !x.Success
                 && x.Message == "disk-full");
    }

    [Fact]
    public async Task 运行中与离线设备分别被门禁拒绝()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var backup = await CreateBackupAsync(client, admin);
        var token = await DevTokenAsync(client, [PermissionCatalog.BackupPush]);

        var gate = await RegisterDeviceAsync(token, "PUSH-GATE");
        await using var connection = gate.Connection;

        await connection.InvokeAsync<DeviceSummaryDto>(
            DeviceHubMethods.ReportStationState,
            new DeviceStationStateRequest(DeviceStationStatuses.Online));

        var busy = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            admin,
            new PushBackupRequest(gate.DeviceId, null, "1.0.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.DevicesBusy,
            await IdentityTestHelpers.ReadErrorCodeAsync(busy, HttpStatusCode.Conflict));

        await connection.InvokeAsync<DeviceSummaryDto>(
            DeviceHubMethods.ReportStationState,
            new DeviceStationStateRequest(DeviceStationStatuses.Idle));
        await connection.StopAsync();

        var offline = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            admin,
            new PushBackupRequest(gate.DeviceId, null, "1.0.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.DevicesOffline,
            await IdentityTestHelpers.ReadErrorCodeAsync(offline, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 设备不回执按超时返回且失败回执不算成功()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var backup = await CreateBackupAsync(client, admin);
        var token = await DevTokenAsync(client, [PermissionCatalog.BackupPush]);

        var silentDevice = await RegisterDeviceAsync(token, "PUSH-SILENT");
        await using var silent = silentDevice.Connection;
        var timeout = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            admin,
            new PushBackupRequest(silentDevice.DeviceId, null, "1.0.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.BackupsAckTimeout,
            await IdentityTestHelpers.ReadErrorCodeAsync(timeout, HttpStatusCode.GatewayTimeout));
        await silent.StopAsync();

        var failingDevice = await RegisterDeviceAsync(token, "PUSH-FAIL");
        await using var failing = failingDevice.Connection;
        failing.On<DeviceConfigPush>(
            DeviceHubEvents.ConfigPush,
            async command => await failing.InvokeAsync(
                DeviceHubMethods.AcknowledgeConfigPush,
                new DeviceConfigPushAck(command.BackupId, false, "disk-full")));

        var result = await PushAsync(client, admin, backup.Id, failingDevice.DeviceId, restartAfterApply: false);
        Assert.True(result.Acked);
        Assert.False(result.Success);
        Assert.Equal("disk-full", result.Message);
    }

    [Fact]
    public async Task 管理端重启在线设备并拒绝离线与无权限令牌()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var token = await DevTokenAsync(client, [PermissionCatalog.DeviceRead, PermissionCatalog.DeviceRestart]);
        var viewer = await DevTokenAsync(client, [PermissionCatalog.DeviceRead]);

        var device = await RegisterDeviceAsync(token, "PUSH-RESTART");
        await using var connection = device.Connection;
        var restarted = new TaskCompletionSource<DeviceRestart>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<DeviceRestart>(
            DeviceHubEvents.Restart,
            command => restarted.TrySetResult(command));

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/devices/{device.DeviceId}/restart",
            viewer,
            new DeviceRestartRequest("维护窗口")));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));

        var accepted = await IdentityTestHelpers.ReadDataAsync<DeviceRestartAccepted>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/devices/{device.DeviceId}/restart",
                admin,
                new DeviceRestartRequest("升级维护"))));
        Assert.Equal(device.DeviceId, accepted.DeviceId);

        var command = await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("升级维护", command.Reason);

        await connection.StopAsync();
        var offline = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/devices/{device.DeviceId}/restart",
            admin,
            new DeviceRestartRequest("离线重启")));
        Assert.Equal(
            (int)ApiErrorCode.DevicesOffline,
            await IdentityTestHelpers.ReadErrorCodeAsync(offline, HttpStatusCode.Conflict));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await db.Set<AuditLog>()
            .Where(x => x.Action == "devices.restart" && x.TargetType == "device")
            .ToListAsync();
        Assert.Contains(
            audits,
            x => x.Result == AuditResults.Success
                 && x.TargetId == device.DeviceId.ToString());
        Assert.Contains(audits, x => x.Result == AuditResults.Failure);
    }

    [Fact]
    public async Task 大备份只下发下载地址且跨设备需显式确认()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var payload = "\"" + new string('x', BackupConventions.InlineMaxBytes + 1024) + "\"";
        var backup = await CreateBackupAsync(client, admin, payload, Guid.NewGuid());
        Assert.True(backup.HasFile);

        var token = await DevTokenAsync(client, [PermissionCatalog.BackupPush]);
        var device = await RegisterDeviceAsync(token, "PUSH-CROSS");
        await using var connection = device.Connection;
        var received = new TaskCompletionSource<DeviceConfigPush>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<DeviceConfigPush>(
            DeviceHubEvents.ConfigPush,
            async command =>
            {
                received.TrySetResult(command);
                await connection.InvokeAsync(
                    DeviceHubMethods.AcknowledgeConfigPush,
                    new DeviceConfigPushAck(command.BackupId, true, null));
            });

        var unconfirmed = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            admin,
            new PushBackupRequest(device.DeviceId, null, "1.0.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.BackupsPushRejected,
            await IdentityTestHelpers.ReadErrorCodeAsync(unconfirmed, HttpStatusCode.Conflict));

        var result = await IdentityTestHelpers.ReadDataAsync<PushBackupResult>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                admin,
                new PushBackupRequest(device.DeviceId, null, "1.0.0", false, false, null, true))));
        Assert.True(result.Acked);

        var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(command.PayloadJson);
        Assert.Equal($"/api/v1/client/backups/{backup.Id:D}/download", command.DownloadUrl);
        Assert.Equal(backup.Sha256, command.Sha256);
    }

    private sealed record TestDevice(HubConnection Connection, Guid DeviceId);

    private async Task<TestDevice> RegisterDeviceAsync(string token, string displayName)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, DeviceHubPaths.Root),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();

        await connection.StartAsync();
        var installGuid = Guid.NewGuid();
        var registered = await connection.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterDevice,
            new DeviceRegistrationRequest(
                new DeviceIdentity(
                    MacAddress: null,
                    InstallGuid: installGuid,
                    FrameworkVersion: "1.0.0",
                    PrimaryIdentifier: $"push-{installGuid:N}"),
                displayName,
                IpAddress: "10.9.8.7"));
        return new TestDevice(connection, registered.DeviceId);
    }

    private static async Task<string> DevTokenAsync(HttpClient client, string[] permissions)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"device-push-{Guid.NewGuid():N}",
                Name: DemoAppKey,
                Permissions: permissions,
                Apps: [DemoAppKey],
                AppKey: DemoAppKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }

    private static async Task<BackupDto> CreateBackupAsync(
        HttpClient client,
        string token,
        string payload = """{"theme":"dark"}""",
        Guid? deviceId = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var namespaceKey = $"push-{suffix}";
        await IdentityTestHelpers.ReadDataAsync<BackupNamespaceDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups/namespaces",
                token,
                new CreateBackupNamespaceRequest(namespaceKey, "设备下发", "集成测试", null, null))));

        return await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups",
                token,
                new CreateBackupRequest(
                    namespaceKey,
                    BackupConventions.KindConfig,
                    "device-push",
                    DeviceId: deviceId,
                    DeviceName: null,
                    ModuleId: null,
                    SchemaVersion: 1,
                    FrameworkVersion: "1.0.0",
                    Channel: null,
                    PayloadJson: payload,
                    Sha256: null))));
    }

    private static async Task<PushBackupResult> PushAsync(
        HttpClient client,
        string token,
        Guid backupId,
        Guid? deviceId,
        bool restartAfterApply)
    {
        return await IdentityTestHelpers.ReadDataAsync<PushBackupResult>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backupId}/push",
                token,
                new PushBackupRequest(deviceId, null, "1.0.0", restartAfterApply, false, null, false))));
    }
}
