using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Persistence.Entities;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Domain;

namespace Platform.IntegrationTests.Api;

/// <summary>设备管理端：权限门槛、应用隔离与审计。注册/心跳走 Hub，不在 REST 上重复开口。</summary>
public sealed class DevicesEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DevicesEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 管理端按权限与应用上下文隔离设备()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"dev-a-{suffix}";
        var appB = $"dev-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var deviceA = await SeedDeviceAsync(appA, "设备A");
        var deviceB = await SeedDeviceAsync(appB, "设备B");

        var viewerA = await DevTokenAsync(
            client, appA, [PermissionCatalog.DeviceRead]);
        var managerA = await DevTokenAsync(
            client, appA, [PermissionCatalog.DeviceRead, PermissionCatalog.DeviceManage]);

        // 列表只看得到当前应用的设备
        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<DeviceSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/devices", viewerA)));
        Assert.Equal(1, list.Total);
        Assert.Equal(deviceA.Id, list.Items[0].Id);

        // 跨应用详情表现为不存在，不泄露其它应用有没有这台设备
        var foreign = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/devices/{deviceB.Id}", viewerA));
        Assert.Equal(
            (int)ApiErrorCode.DevicesNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(foreign, HttpStatusCode.NotFound));

        // 只读令牌不能改名
        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/devices/{deviceA.Id}",
            viewerA,
            new UpdateDeviceRequest(CustomName: "越权改名")));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));

        // 改名落库并写成功审计
        var updated = await IdentityTestHelpers.ReadDataAsync<DeviceDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/devices/{deviceA.Id}",
                managerA,
                new UpdateDeviceRequest(CustomName: "老化台"))));
        Assert.Equal("老化台", updated.CustomName);
        Assert.Equal("老化台-设备A", updated.DisplayName);

        // 目标不存在时返回 404，并留下失败审计
        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/devices/{Guid.NewGuid()}",
            managerA,
            new UpdateDeviceRequest(CustomName: "无主改名")));
        Assert.Equal(
            (int)ApiErrorCode.DevicesNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.NotFound));

        // 平台管理员令牌默认应用上下文是 demo，看不到业务应用的设备
        var adminList = await IdentityTestHelpers.ReadDataAsync<PageResult<DeviceSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/devices", admin)));
        Assert.DoesNotContain(adminList.Items, x => x.Id == deviceA.Id || x.Id == deviceB.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await db.Set<AuditLog>()
            .Where(x => x.Action == "devices.update" && x.TargetType == "device")
            .ToListAsync();
        Assert.Contains(
            audits,
            x => x.Result == AuditResults.Success && x.TargetId == deviceA.Id.ToString());
        Assert.Contains(audits, x => x.Result == AuditResults.Failure);
    }

    private static async Task CreateAppAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                token,
                new CreateAppRequest(key, key))));

    private static async Task<string> DevTokenAsync(
        HttpClient client, string appKey, string[] permissions)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"dev-{appKey}",
                Name: appKey,
                Permissions: permissions,
                Apps: [appKey],
                AppKey: appKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }

    private async Task<Device> SeedDeviceAsync(string appKey, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var device = new Device
        {
            AppKey = appKey,
            DeviceKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            MachineCode = "0123456789ABCDEF",
            InstallGuid = Guid.NewGuid(),
            AutoName = displayName,
            DisplayName = displayName,
        };
        db.Add(device);
        await db.SaveChangesAsync();
        return device;
    }
}
