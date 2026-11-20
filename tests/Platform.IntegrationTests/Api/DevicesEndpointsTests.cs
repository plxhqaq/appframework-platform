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

    [Fact]
    public async Task 安装实例分支按关键词模糊筛选()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appKey = $"dev-br-{suffix}";
        await CreateAppAsync(client, admin, appKey);
        var viewer = await DevTokenAsync(client, appKey, [PermissionCatalog.InstallationRead]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var node = new DeviceNode
        {
            MachineCode = Guid.NewGuid().ToString("N").ToUpperInvariant()[..16],
            AutoName = "分支测试机",
            DisplayName = "分支测试机",
        };
        db.Add(node);
        var rows = new[] { "line-a-2026", "line-b-2026", "coater-line-a" }
            .Select(branch => new DeviceInstallation
            {
                Node = node,
                AppKey = appKey,
                InstallGuid = Guid.NewGuid(),
                Branch = branch,
            })
            .ToArray();
        db.AddRange(rows);
        await db.SaveChangesAsync();

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<InstallationSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/installations?branch=line-a", viewer)));

        Assert.Equal(2, page.Total);
        Assert.Equal(
            new[] { "coater-line-a", "line-a-2026" },
            page.Items.Select(x => x.Branch).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 安装实例与节点删除按在线与子实例受控()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var appA = $"del-a-{suffix}";
        var appB = $"del-b-{suffix}";
        await CreateAppAsync(client, admin, appA);
        await CreateAppAsync(client, admin, appB);

        var (node, (installationA, installationB)) = await SeedSharedNodeAsync(appA, appB, "删除测试机");
        var managerA = await DevTokenAsync(client, appA, [PermissionCatalog.InstallationManage]);
        var managerB = await DevTokenAsync(client, appB, [PermissionCatalog.InstallationManage]);
        var nodeManager = await DevTokenAsync(client, appA, [PermissionCatalog.NodeManage]);

        // 节点还挂着安装实例：必须先逐应用删子行，数据库 Restrict 也不兜底业务提示
        var nodeBusy = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/nodes/{node.Id}", nodeManager));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(nodeBusy, HttpStatusCode.Conflict));

        // 离线且无连接的安装实例可直接删除
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/installations/{installationA.Id}", managerA))).StatusCode);

        // 跨应用删除与不存在同码，不泄露对方应用有没有这个安装实例
        var foreign = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/installations/{installationB.Id}", managerA));
        Assert.Equal(
            (int)ApiErrorCode.DevicesInstallationNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(foreign, HttpStatusCode.NotFound));

        // 状态仍是在线时拒绝（离线扫描在测试宿主里关闭，这里可以稳定复现）
        await SetInstallationOnlineAsync(installationB.Id, DeviceOnlineStatuses.Online);
        var online = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/installations/{installationB.Id}", managerB));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(online, HttpStatusCode.Conflict));

        await SetInstallationOnlineAsync(installationB.Id, DeviceOnlineStatuses.Offline);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/installations/{installationB.Id}", managerB))).StatusCode);

        // 子行清空后节点可删，重复删除回到不存在
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/nodes/{node.Id}", nodeManager))).StatusCode);
        var nodeGone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/nodes/{node.Id}", nodeManager));
        Assert.Equal(
            (int)ApiErrorCode.DevicesNodeNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(nodeGone, HttpStatusCode.NotFound));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.Set<DeviceNode>().AnyAsync(x => x.Id == node.Id));
        Assert.False(await db.Set<DeviceInstallation>()
            .AnyAsync(x => x.Id == installationA.Id || x.Id == installationB.Id));

        var audits = await db.Set<AuditLog>()
            .Where(x => x.Action == "installations.delete" || x.Action == "nodes.delete")
            .ToListAsync();
        Assert.Contains(
            audits,
            x => x.Action == "installations.delete"
                && x.Result == AuditResults.Success
                && x.TargetId == installationA.Id.ToString());
        Assert.Contains(audits, x => x.Action == "installations.delete" && x.Result == AuditResults.Failure);
        Assert.Contains(
            audits,
            x => x.Action == "nodes.delete"
                && x.Result == AuditResults.Success
                && x.TargetId == node.Id.ToString());
    }

    private async Task SetInstallationOnlineAsync(Guid installationId, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.Set<DeviceInstallation>().FirstAsync(x => x.Id == installationId);
        row.OnlineStatus = status;
        await db.SaveChangesAsync();
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
