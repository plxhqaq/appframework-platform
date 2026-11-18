using System.Security.Cryptography;
using Platform.Contracts.Licensing;

namespace Platform.UnitTests.Licensing;

/// <summary>
/// 授权编解码：Crockford Base32、AF4 离线码（签名/应用/产品/机器码绑定）、
/// 在线快照签名与验签。私钥只在测试进程内生成，不落盘。
/// </summary>
public sealed class LicenseCodecTests
{
    private static readonly Lazy<TestKeyPair> Keys = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string MachineA = MachineFromHex("00112233445566778899");
    private static readonly string MachineB = MachineFromHex("AABBCCDDEEFF00112233");

    [Fact]
    public void 离线码签发后可用公钥验签且携带产品与编号()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var notAfter = issued.AddDays(365);

        string code = OfflineLicenseCodec.Encode(
            "demo", "erp-pro", MachineA, LicenseConventions.KindOfficial, issued, notAfter, 0, Keys.Value.PrivateKey, "LIC-001");

        Assert.StartsWith(OfflineLicenseCodec.Prefix, code);
        Assert.True(
            OfflineLicenseCodec.TryParse(code, [Keys.Value.PublicKey], out OfflineLicenseCodeContent content, out string? error),
            error);
        Assert.Equal("demo", content.AppKey);
        Assert.Equal("erp-pro", content.ProductKey);
        Assert.Equal(MachineA, content.MachineCode);
        Assert.Equal(LicenseConventions.KindOfficial, content.Kind);
        Assert.Equal(issued, content.IssuedAt);
        Assert.Equal(notAfter, content.NotAfter);
        Assert.Equal("LIC-001", content.LicenseNo);

        // 人读格式（每 8 字符插 '-'）仍可粘贴解析
        string formatted = Base32Crockford.FormatGroups(code, 8);
        Assert.True(OfflineLicenseCodec.TryParse(formatted, [Keys.Value.PublicKey], out _, out _));
    }

    [Fact]
    public void 离线码篡改一个字符即被拒绝()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string code = OfflineLicenseCodec.Encode(
            "demo", "erp-pro", MachineA, LicenseConventions.KindOfficial, issued, issued.AddDays(30), 0,
            Keys.Value.PrivateKey);

        int index = OfflineLicenseCodec.Prefix.Length + 10;
        char original = code[index];
        char replacement = original == '0' ? '1' : '0';
        string tampered = code[..index] + replacement + code[(index + 1)..];

        Assert.False(OfflineLicenseCodec.TryParse(tampered, [Keys.Value.PublicKey], out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 离线码用未知公钥验签失败()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string code = OfflineLicenseCodec.Encode(
            "demo", "erp-pro", MachineA, LicenseConventions.KindOfficial, issued, issued.AddDays(30), 0,
            Keys.Value.PrivateKey);

        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string otherPublic = LicenseCryptography.ExportPublicKeySpkiBase64(other);

        Assert.False(OfflineLicenseCodec.TryParse(code, [otherPublic], out _, out string? error));
        Assert.Contains("验签失败", error);
    }

    [Fact]
    public void 离线码仍携带机器码供调用方做绑定判定()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string code = OfflineLicenseCodec.Encode(
            "demo", "erp-pro", MachineA, LicenseConventions.KindTrial, issued, issued.AddDays(7), 0,
            Keys.Value.PrivateKey);

        Assert.True(
            OfflineLicenseCodec.TryParse(code, [Keys.Value.PublicKey], out OfflineLicenseCodeContent content, out _));
        Assert.NotEqual(MachineB, content.MachineCode);
        Assert.Equal(LicenseConventions.KindTrial, content.Kind);
        Assert.Equal("demo", content.AppKey);
    }

    [Fact]
    public void 应用键不合法的离线码被拒绝()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.ThrowsAny<ArgumentException>(() => OfflineLicenseCodec.Encode(
            "Demo", "erp-pro", MachineA, LicenseConventions.KindOfficial, issued, issued.AddDays(30), 0,
            Keys.Value.PrivateKey));
    }

    [Fact]
    public void AF3离线码按版本不支持拒绝()
    {
        Assert.False(OfflineLicenseCodec.TryParse("AF3ABCDEFGHIJ", [Keys.Value.PublicKey], out _, out string? error));
        Assert.Contains("版本不支持", error);
    }

    [Fact]
    public void 在线快照签名验签并拒绝篡改与未知密钥()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var payload = new LicenseSnapshotPayload(
            "demo",
            "erp-pro",
            MachineA,
            DeviceCode: null,
            LicenseConventions.KindOfficial,
            LicenseConventions.StatusActive,
            now.AddDays(-1),
            now.AddDays(364),
            GraceDays: 7,
            now,
            "LIC-002",
            Keys.Value.KeyId);

        string token = LicenseSnapshotCodec.Sign(payload, Keys.Value.PrivateKey);
        var trusted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Keys.Value.KeyId] = Keys.Value.PublicKey,
        };

        Assert.True(LicenseSnapshotCodec.TryVerify(token, trusted, out LicenseSnapshotPayload parsed, out string? error), error);
        Assert.Equal("demo", parsed.AppKey);
        Assert.Equal("erp-pro", parsed.ProductKey);
        Assert.Equal(MachineA, parsed.MachineCode);
        Assert.Equal(LicenseConventions.StatusActive, parsed.Status);
        Assert.Equal("LIC-002", parsed.LicenseNo);
        Assert.Equal(Keys.Value.KeyId, parsed.KeyId);

        // 篡改 Base64 内容
        byte[] raw = Convert.FromBase64String(token);
        raw[20] ^= 0xFF;
        string tampered = Convert.ToBase64String(raw);
        Assert.False(LicenseSnapshotCodec.TryVerify(tampered, trusted, out _, out _));

        // 未知公钥
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unknown = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lk-other"] = LicenseCryptography.ExportPublicKeySpkiBase64(other),
        };
        Assert.False(LicenseSnapshotCodec.TryVerify(token, unknown, out _, out _));
    }

    [Fact]
    public void 机器码规范化接受分隔符与易混字符但拒绝错误长度()
    {
        // 机器码是 10 字节哈希的 Crockford 编码（16 字符），不是原始十六进制
        string grouped = $"{MachineA[..4]}-{MachineA[4..8]} {MachineA[8..12]}-{MachineA[12..]}";
        Assert.True(LicenseConventions.TryNormalizeMachineCode(grouped, out string normalized));
        Assert.Equal(MachineA, normalized);

        // 现场手抄常把 0/1 写成 O/I，解码侧应纠正
        string confused = MachineA.Replace('0', 'O').Replace('1', 'I');
        Assert.True(LicenseConventions.TryNormalizeMachineCode(confused, out string recovered));
        Assert.Equal(MachineA, recovered);

        Assert.False(LicenseConventions.TryNormalizeMachineCode("0011", out _));
        Assert.False(LicenseConventions.TryNormalizeMachineCode("0011223344556677UU", out _));
        Assert.False(LicenseConventions.TryNormalizeMachineCode(null, out _));
    }

    [Fact]
    public void Crockford编码解码忽略分隔符并纠正易混字符()
    {
        byte[] data = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0xDC];
        string encoded = Base32Crockford.Encode(data);
        Assert.Equal(16, encoded.Length);

        Assert.True(Base32Crockford.TryDecode(Base32Crockford.FormatGroups(encoded, 4), out byte[] decoded));
        Assert.Equal(data, decoded);

        // O → 0、I/L → 1（末尾填充位为 0 才接受）
        Assert.True(Base32Crockford.TryDecode("OI00", out byte[] confused));
        Assert.Equal(new byte[] { 0x00, 0x40 }, confused);
        Assert.False(Base32Crockford.TryDecode("UUUU", out _));
        Assert.False(Base32Crockford.TryDecode("OI01", out _));
    }

    private static string MachineFromHex(string hex) =>
        Base32Crockford.Encode(Convert.FromHexString(hex));

    private static TestKeyPair Create()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = LicenseCryptography.ExportPublicKeySpkiBase64(key);
        return new TestKeyPair(
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            publicKey,
            LicenseCryptography.ComputeKeyId(publicKey));
    }

    private sealed record TestKeyPair(string PrivateKey, string PublicKey, string KeyId);
}
