using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Backups;
using Platform.Contracts.Devices;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 客户端备份端点：按设备上传/列表/下载、device-backups 自动创建、设备名/分支回填、
/// 跨应用拒绝与 Idempotency-Key。
/// </summary>
public sealed class ClientBackupsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public ClientBackupsEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 按设备上传自动建命名空间并回填设备名()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = await CreateAppAsync(client, admin);
        var token = await DevTokenAsync(
            client, appKey, [PermissionCatalog.BackupCreate, PermissionCatalog.BackupRead]);
        var device = await RegisterDeviceAsync(client, appKey, "CLIENT-01");
        await using var connection = device.Connection;

        const string payload = """{"theme":"light"}""";
        var backup = await UploadAsync(client, token, new CreateBackupRequest(
            NamespaceKey: null,
            Kind: BackupConventions.KindConfig,
            Name: "client-backup",
            DeviceId: device.DeviceId,
            DeviceName: null,
            ModuleId: null,
            SchemaVersion: 1,
            FrameworkVersion: "1.0.0",
            Channel: null,
            PayloadJson: payload,
            Sha256: null));

        Assert.Equal(BackupConventions.DeviceBackupsNamespaceKey, backup.NamespaceKey);
        Assert.Equal("CLIENT-01", backup.DeviceName);
        Assert.Equal(BackupConventions.DefaultChannel, backup.Channel);
        Assert.True(backup.HasInline);
        Assert.Equal(payload, await DownloadAsync(client, token, backup.Id));

        var list = await ListAsync(client, token, $"deviceId={device.DeviceId}");
        Assert.Equal(1, list.Total);
        Assert.Equal(backup.Id, list.Items[0].Id);
    }

    [Fact]
    public async Task 大载荷转存且列表支持设备类型与分页过滤()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appKey = await CreateAppAsync(client, admin);
        var token = await DevTokenAsync(
            client, appKey, [PermissionCatalog.BackupCreate, PermissionCatalog.BackupRead]);
        var device = await RegisterDeviceAsync(client, appKey, "CLIENT-02");
        await using var connection = device.Connection;

        var largePayload = "\"" + new string('x', BackupConventions.InlineMaxBytes + 1024) + "\"";
        var config = await UploadAsync(client, token, NewRequest(device.DeviceId, "big-config", largePayload, BackupConventions.KindConfig));
        Assert.True(config.HasFile);
        Assert.False(config.HasInline);
        Assert.Equal(largePayload, await DownloadAsync(client, token, config.Id));

        var profile = await UploadAsync(client, token, NewRequest(device.DeviceId, "small-profile", """{"p":1}""", BackupConventions.KindProfile));

        var profiles = await ListAsync(client, token, $"deviceId={device.DeviceId}&kind=profile");
        var item = Assert.Single(profiles.Items);
        Assert.Equal(profile.Id, item.Id);

        var page = await ListAsync(client, token, $"deviceId={device.DeviceId}&page=1&pageSize=1");
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task 跨应用未注册设备与幂等上传分别处理()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var appA = await CreateAppAsync(client, admin);
        var appB = await CreateAppAsync(client, admin);
        var tokenA = await DevTokenAsync(
            client, appA, [PermissionCatalog.BackupCreate, PermissionCatalog.BackupRead]);
        var tokenB = await DevTokenAsync(client, appB, [PermissionCatalog.BackupCreate]);
        var device = await RegisterDeviceAsync(client, appA, "CLIENT-03");
        await using var connection = device.Connection;

        // 跨应用：设备存在但不属于当前应用
        var foreign = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/backups",
            tokenB,
            NewRequest(device.DeviceId, "foreign", """{"a":1}""", BackupConventions.KindConfig)));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(foreign, HttpStatusCode.Forbidden));

        // 未注册设备
        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/backups",
            tokenA,
            NewRequest(Guid.NewGuid(), "missing", """{"a":1}""", BackupConventions.KindConfig)));
        Assert.Equal(
            (int)ApiErrorCode.DevicesNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.NotFound));

        // 缺 deviceId
        var noDevice = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/backups",
            tokenA,
            NewRequest(null, "no-device", """{"a":1}""", BackupConventions.KindConfig)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(noDevice, HttpStatusCode.BadRequest));

        // 显式指定不存在的命名空间：不自动创建
        var missingNamespace = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/backups",
            tokenA,
            NewRequest(device.DeviceId, "bad-namespace", """{"a":1}""", BackupConventions.KindConfig) with
            {
                NamespaceKey = $"missing-{Guid.NewGuid():N}"[..20],
            }));
        Assert.Equal(
            (int)ApiErrorCode.BackupsNamespaceNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missingNamespace, HttpStatusCode.NotFound));

        // 幂等键：同键重放同一结果，不新增记录
        var key = Guid.NewGuid().ToString("N");
        var first = await UploadAsync(
            client, tokenA,
            NewRequest(device.DeviceId, "idem", """{"a":1}""", BackupConventions.KindConfig),
            idempotencyKey: key);
        var second = await UploadAsync(
            client, tokenA,
            NewRequest(device.DeviceId, "idem", """{"a":1}""", BackupConventions.KindConfig),
            idempotencyKey: key);
        Assert.Equal(first.Id, second.Id);

        var list = await ListAsync(client, tokenA, $"deviceId={device.DeviceId}");
        Assert.Equal(1, list.Total);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var actions = await db.Set<AuditLog>()
            .Where(x => x.Action.StartsWith("backups.client."))
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Contains("backups.client.upload", actions);
        Assert.Contains("backups.client.list", actions);
    }

    private static CreateBackupRequest NewRequest(
        Guid? deviceId,
        string name,
        string payload,
        string kind) =>
        new(
            NamespaceKey: null,
            Kind: kind,
            Name: name,
            DeviceId: deviceId,
            DeviceName: null,
            ModuleId: null,
            SchemaVersion: 1,
            FrameworkVersion: "1.0.0",
            Channel: null,
            PayloadJson: payload,
            Sha256: null);

    private static async Task<BackupDto> UploadAsync(
        HttpClient client,
        string token,
        CreateBackupRequest request,
        string? idempotencyKey = null)
    {
        using var message = IdentityTestHelpers.Authorized(
            HttpMethod.Post, "/api/v1/client/backups", token, request);
        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(PlatformHeaders.IdempotencyKey, idempotencyKey);
        }

        return await IdentityTestHelpers.ReadDataAsync<BackupDto>(await client.SendAsync(message));
    }

    private static async Task<PageResult<BackupDto>> ListAsync(
        HttpClient client,
        string token,
        string query) =>
        await IdentityTestHelpers.ReadDataAsync<PageResult<BackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/client/backups?{query}", token)));

    private static async Task<string> DownloadAsync(HttpClient client, string token, Guid id)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/backups/{id}/download", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> CreateAppAsync(HttpClient client, string adminToken)
    {
        var key = $"cli-{Guid.NewGuid().ToString("N")[..8]}";
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(key, key))));
        return key;
    }

    private static async Task<string> DevTokenAsync(
        HttpClient client,
        string appKey,
        string[] permissions)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"client-backups-{Guid.NewGuid():N}",
                Name: appKey,
                Permissions: permissions,
                Apps: [appKey],
                AppKey: appKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }

    private async Task<TestDevice> RegisterDeviceAsync(
        HttpClient client,
        string appKey,
        string displayName)
    {
        var token = await DevTokenAsync(client, appKey, ["*"]);
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
                    PrimaryIdentifier: $"client-{installGuid:N}"),
                displayName,
                IpAddress: "10.7.6.5"));
        return new TestDevice(connection, registered.DeviceId);
    }

    private sealed record TestDevice(HubConnection Connection, Guid DeviceId);
}
