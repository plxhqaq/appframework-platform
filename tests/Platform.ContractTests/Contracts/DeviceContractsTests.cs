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
            new DeviceIdentity("AA-BB-CC-DD-EE-FF", Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678"),
                "1.0.0", "SMBIOS-UUID"),
            "PC-01",
            "10.1.2.3",
            "full",
            10,
            "产线A");

        var json = JsonSerializer.Serialize(request, WebJson);
        Assert.Contains("\"displayName\":\"PC-01\"", json, StringComparison.Ordinal);
        Assert.Contains("\"installGuid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"primaryIdentifier\":\"SMBIOS-UUID\"", json, StringComparison.Ordinal);

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
    public void 自定义名作为前缀且重复调用幂等()
    {
        Assert.Equal("产线A-PC-01", DeviceNaming.Compose("PC-01", "产线A"));
        Assert.Equal("PC-01", DeviceNaming.Compose("PC-01", null));
        Assert.Equal("产线A-PC-01", DeviceNaming.Compose("产线A-PC-01", "产线A"));
        Assert.Equal(string.Empty, DeviceNaming.Compose(" ", " "));
    }
}
