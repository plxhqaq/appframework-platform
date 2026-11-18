using System.Text.Json;
using Platform.Contracts.Devices;

namespace Platform.ContractTests.Contracts;

/// <summary>设备契约的跨端形状：camelCase 字段名 + 字符串词表，老端适配器按此映射。</summary>
public sealed class DeviceContractsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void 注册请求按camelCase序列化并可回读()
    {
        var request = new DeviceRegistrationRequest(
            new DeviceIdentity(
                Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678"),
                "4C4C4544-0039-3510-8054-B4C04F4A4D32",
                "C4B4A1D2",
                "AA-BB-CC-DD-EE-FF",
                "1.0.0"),
            "PC-01",
            "10.1.2.3",
            "full",
            10);

        var json = JsonSerializer.Serialize(request, WebJson);
        Assert.Contains("\"displayName\":\"PC-01\"", json, StringComparison.Ordinal);
        Assert.Contains("\"installGuid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"smbiosUuid\":\"4C4C4544-0039-3510-8054-B4C04F4A4D32\"", json, StringComparison.Ordinal);
        Assert.Contains("\"systemDiskSerial\":\"C4B4A1D2\"", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<DeviceRegistrationRequest>(json, WebJson);
        Assert.NotNull(roundTrip);
        Assert.Equal(request, roundTrip);
    }

    [Fact]
    public void 心跳与工位状态只接受词表内的值()
    {
        Assert.True(DeviceHeartbeatStatuses.IsKnown(DeviceHeartbeatStatuses.Idle));
        Assert.False(DeviceHeartbeatStatuses.IsKnown("busy"));
        Assert.True(DeviceStationStatuses.IsKnown(DeviceStationStatuses.Online));
        Assert.False(DeviceStationStatuses.IsKnown("offline"));
        Assert.True(DeviceAlarmLevels.IsKnown(DeviceAlarmLevels.Error));
        Assert.False(DeviceAlarmLevels.IsKnown("fatal"));
        Assert.True(DeviceConfigKinds.IsKnown(DeviceConfigKinds.Profile));
        Assert.False(DeviceConfigKinds.IsKnown("database"));
    }

    [Fact]
    public void 节点展示名按自定义名自动名机器码依次回退()
    {
        Assert.Equal("产线A-PC-01", DeviceNaming.ResolveDisplayName("PC-01", "产线A-PC-01", "MACHINE01"));
        Assert.Equal("PC-01", DeviceNaming.ResolveDisplayName("PC-01", null, "MACHINE01"));
        Assert.Equal("PC-01", DeviceNaming.ResolveDisplayName("PC-01", "  ", "MACHINE01"));
        Assert.Equal("MACHINE01", DeviceNaming.ResolveDisplayName(null, null, "MACHINE01"));
        Assert.Equal("MACHINE01", DeviceNaming.ResolveDisplayName(" ", " ", "MACHINE01"));
    }

    [Fact]
    public void 安装实例展示名按自定义名应用内录入名依次回退()
    {
        Assert.Equal("一号工位", DeviceNaming.ResolveInstallationDisplayName("产线A-扭力", "一号工位"));
        Assert.Equal("产线A-扭力", DeviceNaming.ResolveInstallationDisplayName("产线A-扭力", null));
        Assert.Equal("产线A-扭力", DeviceNaming.ResolveInstallationDisplayName("  产线A-扭力 ", "   "));
        Assert.Null(DeviceNaming.ResolveInstallationDisplayName(null, null));
        Assert.Null(DeviceNaming.ResolveInstallationDisplayName(" ", " "));
    }
}
