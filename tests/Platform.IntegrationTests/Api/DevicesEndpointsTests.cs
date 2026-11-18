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

/// <summary>
/// 节点/安装实例管理端：节点跨应用且只认 node.* 权限；安装实例按应用隔离且只认 installation.* 权限。
/// 注册/心跳走 Hub，不在 REST 上重复开口。
/// </summary>
public sealed class DevicesEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DevicesEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 节点跨应用可见而安装实例按应用隔离()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"dev-a-{suffix}";
        var appB = $"dev-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var (node, (installationA, installationB)) = await SeedSharedNodeAsync(appA, appB, "设备A");

        // 平台管理员能跨应用看到同一物理节点上的两个安装实例
        var nodeDetail = await IdentityTestHelpers.ReadDataAsync<NodeDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/nodes/{node.Id}", admin)));
        Assert.Equal(2, nodeDetail.Installations.Count);
        Assert.Contains(nodeDetail.Installations, x => x.Id == installationA.Id);
        Assert.Contains(nodeDetail.Installations, x => x.Id == installationB.Id);

        // 节点列表按机器码过滤后只有这一个节点
        var nodes = await IdentityTestHelpers.ReadDataAsync<PageResult<NodeSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, $"/api/v1/admin/nodes?keyword={node.MachineCode}", admin)));
        Assert.Equal(1, nodes.Total);
        Assert.Equal(node.Id, nodes.Items[0].Id);

        // 改名落库并写成功审计（节点自定义名覆盖自动名）
        var renamed = await IdentityTestHelpers.ReadDataAsync<NodeDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/nodes/{node.Id}",
                admin,
                new UpdateNodeRequest(CustomName: "老化台"))));
        Assert.Equal("老化台", renamed.DisplayName);
        Assert.Equal("老化台", renamed.CustomName);

        var viewerA = await DevTokenAsync(
            client, appA, [PermissionCatalog.InstallationRead]);
        var managerA = await DevTokenAsync(
            client, appA, [PermissionCatalog.InstallationRead, PermissionCatalog.InstallationManage]);

        // 列表只看得到当前应用的安装实例
        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<InstallationSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/installations", viewerA)));
        Assert.Equal(1, list.Total);
        Assert.Equal(installationA.Id, list.Items[0].Id);

        // 跨应用详情表现为不存在，不泄露其它应用有没有这个安装实例
        var foreign = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/installations/{installationB.Id}", viewerA));
        Assert.Equal(
            (int)ApiErrorCode.DevicesInstallationNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(foreign, HttpStatusCode.NotFound));

        // 只读令牌不能停用安装实例
        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/installations/{installationA.Id}",
            viewerA,
            new UpdateInstallationRequest(IsEnabled: false)));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));

        // 管理令牌可停用并写成功审计
        var updated = await IdentityTestHelpers.ReadDataAsync<InstallationDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/installations/{installationA.Id}",
                managerA,
                new UpdateInstallationRequest(IsEnabled: false))));
        Assert.False(updated.IsEnabled);

        // 目标不存在时返回 404，并留下失败审计
        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/installations/{Guid.NewGuid()}",
            managerA,
            new UpdateInstallationRequest(IsEnabled: false)));
        Assert.Equal(
            (int)ApiErrorCode.DevicesInstallationNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.NotFound));

        // 平台管理员令牌默认应用上下文是 demo，看不到业务应用的安装实例
        var adminList = await IdentityTestHelpers.ReadDataAsync<PageResult<InstallationSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/installations", admin)));
        Assert.DoesNotContain(
            adminList.Items,
            x => x.Id == installationA.Id || x.Id == installationB.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await db.Set<AuditLog>()
            .Where(x => x.Action == "installations.update" && x.TargetType == "device_installation")
            .ToListAsync();
        Assert.Contains(
            audits,
            x => x.Result == AuditResults.Success && x.TargetId == installationA.Id.ToString());
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

    private async Task<(DeviceNode Node, (DeviceInstallation A, DeviceInstallation B) Installations)>
        SeedSharedNodeAsync(string appA, string appB, string autoName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var node = new DeviceNode
        {
            MachineCode = Guid.NewGuid().ToString("N").ToUpperInvariant()[..16],
            AutoName = autoName,
            DisplayName = autoName,
        };
        var installationA = new DeviceInstallation
        {
            Node = node,
            AppKey = appA,
            InstallGuid = Guid.NewGuid(),
        };
        var installationB = new DeviceInstallation
        {
            Node = node,
            AppKey = appB,
            InstallGuid = Guid.NewGuid(),
        };
        db.AddRange(node, installationA, installationB);
        await db.SaveChangesAsync();
        return (node, (installationA, installationB));
    }
}
