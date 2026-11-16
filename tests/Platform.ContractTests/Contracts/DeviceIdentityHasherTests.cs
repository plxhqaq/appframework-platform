using Platform.Contracts.Devices;

namespace Platform.ContractTests.Contracts;

/// <summary>
/// 设备稳定键/机器码向量。两侧（客户端 SDK、服务端）必须得到同一结果；
/// 期望值由独立脚本按文档公式算出，不是从实现里回抄的。
/// </summary>
public sealed class DeviceIdentityHasherTests
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    [Fact]
    public void 主标识与安装Guid决定稳定键和机器码()
    {
        var identity = new DeviceIdentity(
            "AA-BB-CC-DD-EE-FF",
            InstallGuid,
            "1.0.0",
            "AA-BB-CC-DD-EE-FF");

        var (deviceKey, machineCode) = DeviceIdentityHasher.Compute(identity);

        Assert.Equal("9fbba2397410c76759e96d2684d238635390fb17e464dbb49c1ad2e3993f88c2", deviceKey);
        Assert.Equal("BB420AMFHDQWW1QD", machineCode);
        Assert.Equal(DeviceIdentityHasher.ShortIdLength, DeviceIdentityHasher.ShortId(deviceKey).Length);
        Assert.StartsWith(DeviceIdentityHasher.ShortId(deviceKey), deviceKey, StringComparison.Ordinal);
    }

    [Fact]
    public void 带连字符的Smbios标识按规范化结果计算()
    {
        var identity = new DeviceIdentity(
            null,
            InstallGuid,
            "1.0.0",
            "4C4C4544-0039-3510-8054-B4C04F4A4D32");

        var (deviceKey, machineCode) = DeviceIdentityHasher.Compute(identity);

        Assert.Equal("a4a949cfb99cf984773f1b751c00e7ddf7ff24fb166c96d9d873ebda9c65fefd", deviceKey);
        Assert.Equal("JE6WM5F584Z596W9", machineCode);
    }

    [Fact]
    public void 主标识缺失时稳定键只由安装Guid决定且机器码回退()
    {
        var identity = new DeviceIdentity("AA-BB-CC-DD-EE-FF", InstallGuid);

        var (deviceKey, machineCode) = DeviceIdentityHasher.Compute(identity);

        // 老客户端在同一情形下算出的指纹就是 SHA256("|installGuid")；平台必须与之一致
        Assert.Equal("808ee4d8801857c02bf719fc9e56e75902b9d2ceb812ed2a749117ca2dac8033", deviceKey);
        Assert.Equal("R84QHYM4GBER1FP7", machineCode);
    }

    [Fact]
    public void 版本号与MAC不参与身份计算()
    {
        var baseline = DeviceIdentityHasher.Compute(new DeviceIdentity(
            "AA-BB-CC-DD-EE-FF", InstallGuid, "1.0.0", "4C4C4544-0039-3510-8054-B4C04F4A4D32"));

        var changed = DeviceIdentityHasher.Compute(new DeviceIdentity(
            "11-22-33-44-55-66", InstallGuid, "9.9.9", "4C4C4544-0039-3510-8054-B4C04F4A4D32"));

        Assert.Equal(baseline, changed);
    }

    [Fact]
    public void 主标识格式变化不影响稳定键()
    {
        var canonical = DeviceIdentityHasher.Compute(new DeviceIdentity(
            null, InstallGuid, "1.0.0", "4C4C4544003935108054B4C04F4A4D32"));
        var formatted = DeviceIdentityHasher.Compute(new DeviceIdentity(
            null, InstallGuid, "1.0.0", "4c4c4544-0039-3510-8054-b4c04f4a4d32"));

        Assert.Equal(canonical, formatted);
    }

    [Fact]
    public void 安装Guid为空被视为身份不可用()
    {
        var identity = new DeviceIdentity("AA-BB-CC-DD-EE-FF", Guid.Empty, "1.0.0", "AABBCCDDEEFF");

        Assert.Throws<ArgumentException>(() => DeviceIdentityHasher.Compute(identity));
    }

    [Fact]
    public void 机器码为16位Crockford字符()
    {
        var (_, machineCode) = DeviceIdentityHasher.Compute(new DeviceIdentity(
            null, InstallGuid, "1.0.0", "C4B4A1D2"));

        Assert.Equal(DeviceIdentityHasher.MachineCodeLength, machineCode.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{16}$", machineCode);
    }

    [Fact]
    public void 主标识与回退指纹都为空时机器码为空()
    {
        Assert.Equal(string.Empty, DeviceIdentityHasher.ComputeMachineCode(null, null));
        Assert.Equal(string.Empty, DeviceIdentityHasher.ComputeMachineCode("  ", " "));
    }

    [Theory]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("  aa bb cc  ", "AABBCC")]
    public void 标识规范化只保留字母数字并大写(string input, string expected)
    {
        Assert.Equal(expected, DeviceIdentityHasher.NormalizeIdentifier(input));
    }
}
