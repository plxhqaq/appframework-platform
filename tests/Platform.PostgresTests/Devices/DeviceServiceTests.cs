using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Options;
using Platform.Modules.Devices.Services;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests.Devices;

/// <summary>设备域的真实 PostgreSQL 契约：节点/安装实例去重、状态、离线扫描、跨应用隔离与管理端查询。</summary>
public sealed class DeviceServiceTests
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    private static DeviceIdentity Identity(string primary, Guid? installGuid = null) =>
        new(installGuid ?? InstallGuid, primary, SystemDiskSerial: null, MacAddress: "AA-BB-CC-DD-EE-FF", FrameworkVersion: "1.0.0");

    private static DeviceRegistrationRequest Request(
        string primary,
        string autoName = "PC-01",
        string? branch = "full",
        int? heartbeatSeconds = 10,
        Guid? installGuid = null) =>
        new(Identity(primary, installGuid), autoName, "10.1.2.3", branch, heartbeatSeconds);

    private static InstallationRegistry Registry(PlatformDbContext db, IClock clock) =>
        new(db, clock, Options.Create(new DevicesOptions()), NullLogger<InstallationRegistry>.Instance);

    private static InstallationPresence Presence(PlatformDbContext db, IClock clock) =>
        new(db, clock, Options.Create(new DevicesOptions()), NullLogger<InstallationPresence>.Instance);

    private static InstallationAdmin Installations(PlatformDbContext db) => new(db);

    private static NodeAdmin Nodes(PlatformDbContext db) => new(db);

    private static string NewAppKey() => "dev-" + Guid.NewGuid().ToString("N")[..12];

    [PostgresFact]
    public async Task 同一应用同一安装重复注册复用同一安装实例()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid firstId;
        Guid nodeId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var first = await Registry(db, clock).RegisterAsync(appKey, Request("PRIMARY-1"));
            firstId = first.InstallationId;
            nodeId = first.NodeId;
            Assert.Equal(16, first.InstallationShortId.Length);
            Assert.Equal(16, first.NodeShortId.Length);
        }

        DeviceRegistrationResponse second;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            second = await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-1", branch: "torque"));
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var installation = await verify.Set<DeviceInstallation>()
            .Include(static x => x.Node)
            .AsNoTracking()
            .SingleAsync(x => x.AppKey == appKey);

        Assert.Equal(firstId, second.InstallationId);
        Assert.Equal(nodeId, second.NodeId);
        Assert.Equal("PC-01", installation.Node.DisplayName);
        Assert.Equal("torque", installation.Branch);
        Assert.Equal(DeviceOnlineStatuses.Online, installation.OnlineStatus);
        Assert.Equal(1, await verify.Set<DeviceNode>().CountAsync(x => x.Id == nodeId));
    }

    [PostgresFact]
    public async Task 同机不同应用是一个节点两个安装实例()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appA = NewAppKey();
        var appB = NewAppKey();

        DeviceRegistrationResponse first;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            first = await Registry(db, clock).RegisterAsync(
                appA, Request("SHARED-MACHINE", "PC-a", installGuid: Guid.NewGuid()));
        }

        DeviceRegistrationResponse second;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            second = await Registry(db, clock).RegisterAsync(
                appB, Request("SHARED-MACHINE", "PC-b", installGuid: Guid.NewGuid()));
        }

        Assert.Equal(first.NodeId, second.NodeId);
        Assert.NotEqual(first.InstallationId, second.InstallationId);
        Assert.Equal(first.MachineCode, second.MachineCode);

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        Assert.Equal(1, await verify.Set<DeviceNode>().CountAsync(x => x.MachineCode == first.MachineCode));
        Assert.Equal(2, await verify.Set<DeviceInstallation>().CountAsync(x => x.NodeId == first.NodeId));
    }

    [Fact]
    public void 同机不同安装可并存而同安装重连顶替()
    {
        var registry = new InstallationConnectionRegistry();
        var installationA = Guid.NewGuid();
        var installationB = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        Assert.Null(registry.Register("app-a", installationA, nodeId, InstallGuid, "conn-a1"));
        Assert.Null(registry.Register("app-b", installationB, nodeId, InstallGuid, "conn-b1"));
        Assert.Equal(2, registry.CountNodeConnections(nodeId));

        var replaced = registry.Register("app-a", installationA, nodeId, InstallGuid, "conn-a2");
        Assert.Equal("conn-a1", replaced);
        Assert.False(registry.TryGet("conn-a1", out _));
        Assert.True(registry.TryGet("conn-a2", out var currentA));
        Assert.Equal(installationA, currentA.InstallationId);

        registry.Unregister("conn-a2");
        Assert.True(registry.HasConnection(installationB));
        registry.Unregister("conn-b1");
        Assert.False(registry.HasConnection(installationB));
        Assert.Equal(0, registry.Count);
    }

    [PostgresFact]
    public async Task 心跳同时维护在线工位与报警()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid installationId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            installationId = (await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-HB"))).InstallationId;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var snapshot = await Presence(db, clock).HeartbeatAsync(
                appKey,
                installationId,
                new DeviceHeartbeatRequest(
                    DeviceHeartbeatStatuses.Idle,
                    DeviceAlarmLevels.Warning,
                    "气压偏低",
                    clock.UtcNow.AddMinutes(-30)));
            Assert.Equal(DeviceOnlineStatuses.Online, snapshot.OnlineStatus);
            Assert.Equal(DeviceStationStatuses.Idle, snapshot.StationStatus);
            Assert.Equal(DeviceAlarmLevels.Warning, snapshot.AlarmLevel);
            Assert.Equal("气压偏低", snapshot.AlarmMessage);
            // 心跳时间必须以服务端时钟为准，不能被客户端明显落后的时间覆盖
            Assert.Equal(clock.UtcNow, snapshot.LastHeartbeatAt);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var snapshot = await Presence(db, clock).HeartbeatAsync(
                appKey,
                installationId,
                new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
            Assert.Equal(DeviceStationStatuses.Online, snapshot.StationStatus);
            Assert.Null(snapshot.AlarmMessage);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var snapshot = await Presence(db, clock).ReportStationStateAsync(
                appKey, installationId, new DeviceStationStateRequest(DeviceStationStatuses.Idle));
            Assert.Equal(DeviceStationStatuses.Idle, snapshot.StationStatus);
        }
    }

    [PostgresFact]
    public async Task 停用安装实例或节点分别拒绝注册与心跳()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid installationId;
        Guid nodeId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var registered = await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-DISABLED"));
            installationId = registered.InstallationId;
            nodeId = registered.NodeId;
        }

        // 应用级停用安装实例：注册与心跳都被拒绝
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            await Installations(db).UpdateAsync(
                appKey, installationId, new UpdateInstallationRequest(IsEnabled: false));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Registry(db, clock).RegisterAsync(appKey, Request("PRIMARY-DISABLED")));
            Assert.Equal(ApiErrorCode.DevicesInstallationDisabled, exception.Code);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Presence(db, clock).HeartbeatAsync(
                    appKey,
                    installationId,
                    new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None)));
            Assert.Equal(ApiErrorCode.DevicesInstallationDisabled, exception.Code);
        }

        // 平台级停用节点：该机器上所有应用都被拒绝
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            await Nodes(db).UpdateAsync(nodeId, new UpdateNodeRequest(IsEnabled: false));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Registry(db, clock).RegisterAsync(NewAppKey(), Request("PRIMARY-DISABLED")));
            Assert.Equal(ApiErrorCode.DevicesNodeDisabled, exception.Code);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Presence(db, clock).HeartbeatAsync(
                    appKey,
                    installationId,
                    new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None)));
            Assert.Equal(ApiErrorCode.DevicesNodeDisabled, exception.Code);
        }
    }

    [PostgresFact]
    public async Task 离线扫描按心跳阈值标记离线()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid installationId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            installationId = (await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-OFFLINE", heartbeatSeconds: 10))).InstallationId;
        }

        // 刚注册不应被判离线（阈值 max(3×10, 60) = 60 秒）
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            Assert.Equal(0, await Presence(db, clock).MarkOfflineAsync());
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var installation = await db.Set<DeviceInstallation>()
                .SingleAsync(x => x.Id == installationId);
            installation.LastHeartbeatAt = clock.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            Assert.Equal(1, await Presence(db, clock).MarkOfflineAsync());
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var offline = await verify.Set<DeviceInstallation>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == installationId);
        Assert.Equal(DeviceOnlineStatuses.Offline, offline.OnlineStatus);
    }

    [PostgresFact]
    public async Task 安装实例管理端分页过滤与跨应用隔离()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appA = NewAppKey();
        var appB = NewAppKey();

        string machineCodeOfSecond = string.Empty;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var registry = Registry(db, clock);
            await registry.RegisterAsync(
                appA, Request("ADMIN-PRIMARY-1", "PC-01", "full", installGuid: Guid.NewGuid()));
            var second = await registry.RegisterAsync(
                appA, Request("ADMIN-PRIMARY-2", "PC-02", "torque", installGuid: Guid.NewGuid()));
            await registry.RegisterAsync(
                appA, Request("ADMIN-PRIMARY-3", "PC-03", "full", installGuid: Guid.NewGuid()));
            machineCodeOfSecond = (await Installations(db).GetAsync(appA, second.InstallationId)).MachineCode;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            await Registry(db, clock).RegisterAsync(
                appB, Request("ADMIN-PRIMARY-1", installGuid: Guid.NewGuid()));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var admin = Installations(db);
            var all = await admin.ListAsync(appA, new PageQuery { Page = 1, PageSize = 10 }, null, null);
            Assert.Equal(3, all.Total);

            var torque = await admin.ListAsync(appA, new PageQuery(), null, "torque");
            Assert.Equal(1, torque.Total);
            Assert.Equal("PC-02", torque.Items[0].NodeDisplayName);

            var byMachineCode = await admin.ListAsync(
                appA, new PageQuery { Keyword = machineCodeOfSecond }, null, null);
            Assert.Equal(1, byMachineCode.Total);

            var online = await admin.ListAsync(appA, new PageQuery(), DeviceOnlineStatuses.Online, null);
            Assert.Equal(3, online.Total);

            var foreignId = await db.Set<DeviceInstallation>()
                .Where(x => x.AppKey == appB)
                .Select(x => x.Id)
                .SingleAsync();
            var exception = await Assert.ThrowsAsync<BusinessException>(
                () => admin.GetAsync(appA, foreignId));
            Assert.Equal(ApiErrorCode.DevicesInstallationNotFound, exception.Code);

            // 关键字按机器码命中后仍限定在当前应用
            var crossAppKeyword = await admin.ListAsync(
                appB, new PageQuery { Keyword = machineCodeOfSecond }, null, null);
            Assert.Equal(0, crossAppKeyword.Total);
        }
    }

    [PostgresFact]
    public async Task 节点管理端跨应用可见且改自定义名与清除后回落最新自动名()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appA = NewAppKey();
        var appB = NewAppKey();

        Guid nodeId;
        string machineCode;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var registered = await Registry(db, clock).RegisterAsync(
                appA, Request("NODE-PRIMARY-1", "PC-09"));
            nodeId = registered.NodeId;
            machineCode = registered.MachineCode;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            await Registry(db, clock).RegisterAsync(
                appB, Request("NODE-PRIMARY-1", "PC-other", installGuid: Guid.NewGuid()));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var admin = Nodes(db);

            var byMachineCode = await admin.ListAsync(
                new PageQuery { Keyword = machineCode }, null, null);
            Assert.Equal(1, byMachineCode.Total);
            Assert.Equal(2, byMachineCode.Items[0].InstallationCount);
            Assert.Equal(2, byMachineCode.Items[0].AppKeys.Count);

            var online = await admin.ListAsync(new PageQuery(), DeviceOnlineStatuses.Online, null);
            Assert.Contains(online.Items, x => x.Id == nodeId);

            var detail = await admin.GetAsync(nodeId);
            Assert.Equal(2, detail.Installations.Count);

            var renamed = await admin.UpdateAsync(nodeId, new UpdateNodeRequest(CustomName: "老化台"));
            Assert.Equal("老化台", renamed.DisplayName);
            Assert.Equal("老化台", renamed.CustomName);

            var cleared = await admin.UpdateAsync(nodeId, new UpdateNodeRequest(CustomName: string.Empty));
            Assert.Equal("PC-other", cleared.DisplayName);
            Assert.Null(cleared.CustomName);

            var exception = await Assert.ThrowsAsync<BusinessException>(
                () => admin.GetAsync(Guid.NewGuid()));
            Assert.Equal(ApiErrorCode.DevicesNodeNotFound, exception.Code);
        }
    }

    [PostgresFact]
    public async Task 非法设备身份被拒绝()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        await using var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock);

        var noInstall = new DeviceRegistrationRequest(
            new DeviceIdentity(Guid.Empty, "PRIMARY", null),
            "PC-01");
        var installException = await Assert.ThrowsAsync<BusinessException>(() =>
            Registry(db, clock).RegisterAsync(NewAppKey(), noInstall));
        Assert.Equal(ApiErrorCode.DevicesIdentityInvalid, installException.Code);

        var noPrimary = new DeviceRegistrationRequest(
            new DeviceIdentity(Guid.NewGuid(), "Default string", null),
            "PC-01");
        var primaryException = await Assert.ThrowsAsync<BusinessException>(() =>
            Registry(db, clock).RegisterAsync(NewAppKey(), noPrimary));
        Assert.Equal(ApiErrorCode.DevicesPrimaryIdentifierMissing, primaryException.Code);
    }
}
