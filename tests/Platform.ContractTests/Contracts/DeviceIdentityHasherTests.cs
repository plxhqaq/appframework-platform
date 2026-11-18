using Platform.Contracts.Devices;

namespace Platform.ContractTests.Contracts;

/// <summary>
/// 机器码/安装指纹向量。两侧（老客户端、服务端）必须得到同一结果；
/// 期望值由独立脚本按文档公式算出，不是从实现里回抄的。
/// </summary>
public sealed class DeviceIdentityHasherTests
{
    private static readonly Guid InstallGuid = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");

    private const string SmbiosUuid = "4C4C4544-0039-3510-8054-B4C04F4A4D32";
    private const string SmbiosMachineCode = "JE6WM5F584Z596W9";
    private const string DiskSerialMachineCode = "VK49EX3F2K82XCJ5";

    [Fact]
    public void 机器码优先SMBIOS且忽略磁盘序列号与MAC()
    {
        var identity = new DeviceIdentity(
            InstallGuid,
            SmbiosUuid,
            "C4B4A1D2",
            "AA-BB-CC-DD-EE-FF",
            "1.0.0");

        Assert.Equal(SmbiosMachineCode, DeviceIdentityHasher.MachineCode(identity));

        var changed = identity with
        {
            SystemDiskSerial = "OTHER-DISK",
            MacAddress = "11-22-33-44-55-66",
            FrameworkVersion = "9.9.9",
        };

        Assert.Equal(SmbiosMachineCode, DeviceIdentityHasher.MachineCode(changed));
    }

    [Fact]
    public void SMBIOS缺失时回退到系统盘序列号()
    {
        var identity = new DeviceIdentity(InstallGuid, null, "C4B4A1D2");

        Assert.Equal(DiskSerialMachineCode, DeviceIdentityHasher.MachineCode(identity));
    }

    [Theory]
    [InlineData("Default string")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("   ")]
    public void SMBIOS为无效占位值时回退到系统盘序列号(string smbios)
    {
        var identity = new DeviceIdentity(InstallGuid, smbios, "C4B4A1D2");

        Assert.Equal(DiskSerialMachineCode, DeviceIdentityHasher.MachineCode(identity));
    }

    [Fact]
    public void 两个主标识都不可用时拒绝计算()
    {
        var identity = new DeviceIdentity(InstallGuid, "Default string", "FFFFFFFFFFFF");

        Assert.Throws<InvalidOperationException>(() => DeviceIdentityHasher.MachineCode(identity));
    }

    [Fact]
    public void 主标识格式变化不影响机器码()
    {
        var canonical = new DeviceIdentity(InstallGuid, "4C4C4544003935108054B4C04F4A4D32", null);
        var formatted = new DeviceIdentity(InstallGuid, "4c4c4544-0039-3510-8054-b4c04f4a4d32", null);

        Assert.Equal(
            DeviceIdentityHasher.MachineCode(canonical),
            DeviceIdentityHasher.MachineCode(formatted));
    }

    [Fact]
    public void 安装指纹由主标识与安装Guid决定()
    {
        Assert.Equal(
            "a4a949cfb99cf984773f1b751c00e7ddf7ff24fb166c96d9d873ebda9c65fefd",
            DeviceIdentityHasher.InstallationFingerprint(SmbiosUuid, InstallGuid));

        // 老客户端在无主标识时算出的指纹就是 SHA256("|installGuid")；平台必须与之一致
        Assert.Equal(
            "808ee4d8801857c02bf719fc9e56e75902b9d2ceb812ed2a749117ca2dac8033",
            DeviceIdentityHasher.InstallationFingerprint(null, InstallGuid));
    }

    [Fact]
    public void 安装Guid为空视为身份不可用()
    {
        Assert.Throws<ArgumentException>(
            () => DeviceIdentityHasher.InstallationFingerprint(SmbiosUuid, Guid.Empty));
    }

    [Fact]
    public void 机器码为16位Crockford字符()
    {
        var machineCode = DeviceIdentityHasher.MachineCode(
            new DeviceIdentity(InstallGuid, SmbiosUuid, null));

        Assert.Equal(DeviceIdentityHasher.MachineCodeLength, machineCode.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{16}$", machineCode);
    }

    [Fact]
    public void 短ID取Guid前16位十六进制()
    {
        var shortId = DeviceIdentityHasher.ShortId(InstallGuid);

        Assert.Equal(DeviceIdentityHasher.ShortIdLength, shortId.Length);
        Assert.Equal("a1b2c3d4e5f64789", shortId);
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
