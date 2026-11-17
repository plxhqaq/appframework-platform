using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Options;
using Platform.Modules.Devices.Services;
using Platform.PostgresTests.Support;
using Xunit;

namespace Platform.PostgresTests.Devices;

/// <summary>设备域的真实 PostgreSQL 契约：去重、状态、离线扫描、跨应用隔离与管理端查询。</summary>
public sealed class DeviceServiceTests
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    private static DeviceIdentity Identity(string primary, Guid? installGuid = null) =>
        new("AA-BB-CC-DD-EE-FF", installGuid ?? InstallGuid, "1.0.0", primary);

    private static DeviceRegistrationRequest Request(
        string primary,
        string autoName = "PC-01",
        string? customName = null,
        string? branch = "full",
        int? heartbeatSeconds = 10,
        Guid? installGuid = null) =>
        new(Identity(primary, installGuid), autoName, "10.1.2.3", branch, heartbeatSeconds, customName);

    private static DeviceRegistry Registry(PlatformDbContext db, IClock clock) =>
        new(db, clock, Options.Create(new DevicesOptions()), NullLogger<DeviceRegistry>.Instance);

    private static DevicePresence Presence(PlatformDbContext db, IClock clock) =>
        new(db, clock, Options.Create(new DevicesOptions()), NullLogger<DevicePresence>.Instance);

    private static DeviceAdmin Admin(PlatformDbContext db) => new(db);

    private static string NewAppKey() => "dev-" + Guid.NewGuid().ToString("N")[..12];

    [PostgresFact]
    public async Task 同一应用内重复注册复用同一条设备()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid firstId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var first = await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-1", customName: "产线A"));
            firstId = first.DeviceId;
            Assert.Equal(16, first.DeviceShortId.Length);
        }

        DeviceRegistrationResponse second;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            second = await Registry(db, clock).RegisterAsync(
                appKey,
                Request("PRIMARY-1", branch: "torque") with { CustomName = null });
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var device = await verify.Set<Platform.Modules.Devices.Domain.Device>()
            .AsNoTracking()
            .SingleAsync(x => x.AppKey == appKey);

        Assert.Equal(firstId, second.DeviceId);
        Assert.Equal("产线A-PC-01", device.DisplayName);
        Assert.Equal("torque", device.Branch);
        Assert.Equal(DeviceOnlineStatuses.Online, device.OnlineStatus);
    }

    [PostgresFact]
    public async Task 同一机器码不同安装复用同一条设备()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();
        var firstInstall = Guid.NewGuid();
        var secondInstall = Guid.NewGuid();

        DeviceRegistrationResponse first;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            first = await Registry(db, clock).RegisterAsync(
                appKey,
                Request("SHARED-MACHINE", "PC-full", branch: "full", installGuid: firstInstall));
        }

        DeviceRegistrationResponse second;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            second = await Registry(db, clock).RegisterAsync(
                appKey,
                Request("SHARED-MACHINE", "PC-torque", branch: "torque", installGuid: secondInstall));
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var device = await verify.Set<Platform.Modules.Devices.Domain.Device>()
            .AsNoTracking()
            .SingleAsync(x => x.AppKey == appKey);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.DeviceShortId, second.DeviceShortId);
        Assert.Equal(device.MachineCode, device.DeviceKey);
        Assert.Equal(secondInstall, device.InstallGuid);
        Assert.Equal(1, await verify.Set<Platform.Modules.Devices.Domain.Device>().CountAsync(x => x.AppKey == appKey));
    }

    [Fact]
    public void 同机不同安装可并存而同安装重连顶替()
    {
        var registry = new DeviceConnectionRegistry();
        var deviceId = Guid.NewGuid();
        var installA = Guid.NewGuid();
        var installB = Guid.NewGuid();

        Assert.Null(registry.Register("app", deviceId, installA, "conn-a1"));
        Assert.Null(registry.Register("app", deviceId, installB, "conn-b1"));
        Assert.Equal(1, registry.Count);

        var replaced = registry.Register("app", deviceId, installA, "conn-a2");
        Assert.Equal("conn-a1", replaced);
        Assert.False(registry.TryGet("conn-a1", out _));
        Assert.True(registry.TryGet("conn-a2", out var currentA));
        Assert.Equal(installA, currentA.InstallGuid);

        registry.Unregister("conn-a2");
        Assert.True(registry.HasConnection(deviceId));
        registry.Unregister("conn-b1");
        Assert.False(registry.HasConnection(deviceId));
        Assert.Equal(0, registry.Count);
    }

    [PostgresFact]
    public async Task 同一台设备在不同应用下互不影响()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appA = NewAppKey();
        var appB = NewAppKey();

        Guid idA;
        Guid idB;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            idA = (await Registry(db, clock).RegisterAsync(appA, Request("SHARED-PRIMARY"))).DeviceId;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            idB = (await Registry(db, clock).RegisterAsync(appB, Request("SHARED-PRIMARY"))).DeviceId;
        }

        Assert.NotEqual(idA, idB);

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        Assert.Equal(1, await verify.Set<Platform.Modules.Devices.Domain.Device>().CountAsync(x => x.AppKey == appA));
        Assert.Equal(1, await verify.Set<Platform.Modules.Devices.Domain.Device>().CountAsync(x => x.AppKey == appB));
    }

    [PostgresFact]
    public async Task 心跳同时维护在线工位与报警()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid deviceId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            deviceId = (await Registry(db, clock).RegisterAsync(appKey, Request("PRIMARY-HB"))).DeviceId;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var snapshot = await Presence(db, clock).HeartbeatAsync(
                appKey,
                deviceId,
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
                deviceId,
                new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Online, DeviceAlarmLevels.None));
            Assert.Equal(DeviceStationStatuses.Online, snapshot.StationStatus);
            Assert.Null(snapshot.AlarmMessage);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var snapshot = await Presence(db, clock).ReportStationStateAsync(
                appKey, deviceId, new DeviceStationStateRequest(DeviceStationStatuses.Idle));
            Assert.Equal(DeviceStationStatuses.Idle, snapshot.StationStatus);
        }
    }

    [PostgresFact]
    public async Task 停用设备拒绝注册与心跳()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid deviceId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            deviceId = (await Registry(db, clock).RegisterAsync(appKey, Request("PRIMARY-DISABLED"))).DeviceId;
            await Admin(db).UpdateAsync(appKey, deviceId, new UpdateDeviceRequest(IsEnabled: false));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Registry(db, clock).RegisterAsync(appKey, Request("PRIMARY-DISABLED")));
            Assert.Equal(ApiErrorCode.DevicesDisabled, exception.Code);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() =>
                Presence(db, clock).HeartbeatAsync(
                    appKey,
                    deviceId,
                    new DeviceHeartbeatRequest(DeviceHeartbeatStatuses.Idle, DeviceAlarmLevels.None)));
            Assert.Equal(ApiErrorCode.DevicesDisabled, exception.Code);
        }
    }

    [PostgresFact]
    public async Task 离线扫描按心跳阈值标记离线()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid deviceId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            deviceId = (await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-OFFLINE", heartbeatSeconds: 10))).DeviceId;
        }

        // 刚注册不应被判离线（阈值 max(3×10, 60) = 60 秒）
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            Assert.Equal(0, await Presence(db, clock).MarkOfflineAsync());
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var device = await db.Set<Platform.Modules.Devices.Domain.Device>().SingleAsync(x => x.Id == deviceId);
            device.LastHeartbeatAt = clock.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            Assert.Equal(1, await Presence(db, clock).MarkOfflineAsync());
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var offline = await verify.Set<Platform.Modules.Devices.Domain.Device>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == deviceId);
        Assert.Equal(DeviceOnlineStatuses.Offline, offline.OnlineStatus);
    }

    [PostgresFact]
    public async Task 管理端分页过滤与跨应用隔离()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appA = NewAppKey();
        var appB = NewAppKey();

        string machineCodeOfSecond = string.Empty;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var registry = Registry(db, clock);
            await registry.RegisterAsync(appA, Request("ADMIN-PRIMARY-1", "PC-01", "一车间", "full"));
            var second = await registry.RegisterAsync(appA, Request("ADMIN-PRIMARY-2", "PC-02", "一车间", "torque"));
            await registry.RegisterAsync(appA, Request("ADMIN-PRIMARY-3", "PC-03", "二车间", "full"));
            machineCodeOfSecond = (await Admin(db).GetAsync(appA, second.DeviceId)).MachineCode;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            await Registry(db, clock).RegisterAsync(appB, Request("ADMIN-PRIMARY-1"));
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var admin = Admin(db);
            var all = await admin.ListAsync(appA, new PageQuery { Page = 1, PageSize = 10 }, null, null);
            Assert.Equal(3, all.Total);

            var torque = await admin.ListAsync(appA, new PageQuery(), null, "torque");
            Assert.Equal(1, torque.Total);
            Assert.Equal("PC-02", torque.Items[0].AutoName);

            var byMachineCode = await admin.ListAsync(
                appA, new PageQuery { Keyword = machineCodeOfSecond }, null, null);
            Assert.Equal(1, byMachineCode.Total);

            var online = await admin.ListAsync(appA, new PageQuery(), DeviceOnlineStatuses.Online, null);
            Assert.Equal(3, online.Total);

            var foreignId = await db.Set<Platform.Modules.Devices.Domain.Device>()
                .Where(x => x.AppKey == appB)
                .Select(x => x.Id)
                .SingleAsync();
            var exception = await Assert.ThrowsAsync<BusinessException>(() => admin.GetAsync(appA, foreignId));
            Assert.Equal(ApiErrorCode.DevicesNotFound, exception.Code);

            // 关键字按机器码命中后仍限定在当前应用
            var crossAppKeyword = await admin.ListAsync(
                appB, new PageQuery { Keyword = machineCodeOfSecond }, null, null);
            Assert.Equal(0, crossAppKeyword.Total);
        }
    }

    [PostgresFact]
    public async Task 已合并的历史重复设备不再出现在管理端()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();
        var canonicalId = Guid.NewGuid();
        var mergedId = Guid.NewGuid();
        var now = clock.UtcNow;

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            db.Set<Platform.Modules.Devices.Domain.Device>().AddRange(
                new Platform.Modules.Devices.Domain.Device
                {
                    Id = canonicalId,
                    AppKey = appKey,
                    DeviceKey = "MERGEDCODE000001",
                    MachineCode = "MERGEDCODE000001",
                    DisplayName = "主设备",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Platform.Modules.Devices.Domain.Device
                {
                    Id = mergedId,
                    AppKey = appKey,
                    DeviceKey = new string('a', 64),
                    MachineCode = "MERGEDCODE000001",
                    DisplayName = "历史重复",
                    MergedIntoDeviceId = canonicalId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        await using var verify = PostgresTestEnvironment.CreateDbContext(connectionString, clock);
        var admin = Admin(verify);
        var list = await admin.ListAsync(appKey, new PageQuery { Page = 1, PageSize = 10 }, null, null);
        Assert.Equal(1, list.Total);
        Assert.Equal(canonicalId, list.Items[0].Id);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => admin.GetAsync(appKey, mergedId));
        Assert.Equal(ApiErrorCode.DevicesNotFound, exception.Code);
    }

    [PostgresFact]
    public async Task 管理端改自定义名与清除后展示名回落到机器名()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        var appKey = NewAppKey();

        Guid deviceId;
        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            deviceId = (await Registry(db, clock).RegisterAsync(
                appKey, Request("PRIMARY-RENAME", "PC-09"))).DeviceId;
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var renamed = await Admin(db).UpdateAsync(
                appKey, deviceId, new UpdateDeviceRequest(CustomName: "老化台"));
            Assert.Equal("老化台-PC-09", renamed.DisplayName);
            Assert.Equal("老化台", renamed.CustomName);
        }

        await using (var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock))
        {
            var cleared = await Admin(db).UpdateAsync(
                appKey, deviceId, new UpdateDeviceRequest(CustomName: string.Empty));
            Assert.Equal("PC-09", cleared.DisplayName);
            Assert.Null(cleared.CustomName);
        }
    }

    [PostgresFact]
    public async Task 非法设备身份被拒绝()
    {
        var connectionString = await PostgresTestEnvironment.GetConnectionStringAsync();
        var clock = new TestClock();
        await using var db = PostgresTestEnvironment.CreateDbContext(connectionString, clock);

        var request = new DeviceRegistrationRequest(
            new DeviceIdentity(null, Guid.Empty, "1.0.0", "PRIMARY"),
            "PC-01");

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            Registry(db, clock).RegisterAsync(NewAppKey(), request));
        Assert.Equal(ApiErrorCode.DevicesIdentityInvalid, exception.Code);
    }
}
