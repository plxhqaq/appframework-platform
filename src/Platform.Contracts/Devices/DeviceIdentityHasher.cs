using System.Security.Cryptography;
using System.Text;
using Platform.Contracts.Licensing;

namespace Platform.Contracts.Devices;

/// <summary>
/// 机器码与安装指纹算法。纯函数，客户端与服务端必须得到完全一致的结果。
/// </summary>
/// <remarks>
/// <para>
/// 机器码 = SHA-256("AF-MACHINE|" + 规范化主标识) 前 10 字节的 Crockford Base32（16 字符）。
/// 主标识优先 SMBIOS UUID，其次系统盘序列号；两者都不可用时拒绝注册，不再回退到安装 Guid，
/// 因为回退会让同一台机器每次重装都变成新节点，授权席位无法锚定。
/// </para>
/// <para>
/// 安装指纹只用于单次密钥/扫码/会话的设备绑定，不参与节点或安装实例身份。
/// MAC 不参与以上任何计算，只作辅助展示。
/// </para>
/// </remarks>
public static class DeviceIdentityHasher
{
    /// <summary>短 ID 长度（Guid 的 N 格式前 16 位十六进制字符）。</summary>
    public const int ShortIdLength = 16;

    /// <summary>机器码字符长度（Crockford Base32，10 字节哈希 = 16 字符）。</summary>
    public const int MachineCodeLength = 16;

    /// <summary>参与机器码哈希的字节数（80 bit，碰撞概率可忽略）。</summary>
    private const int MachineCodeHashBytes = 10;

    private const string MachineCodePrimaryPrefix = "AF-MACHINE|";

    private static readonly HashSet<string> InvalidSmbiosValues = new(StringComparer.Ordinal)
    {
        "NOTSPECIFIED",
        "NOTAPPLICABLE",
        "DEFAULTSTRING",
        "TOBEFILLEDBYOEM",
        "SYSTEMSERIALNUMBER",
        "UNKNOWN",
        "INVALID",
        "NONE",
        "OEM",
    };

    /// <summary>
    /// 计算全局机器码。主标识缺失或只有无效占位值时抛 <see cref="InvalidOperationException"/>，
    /// 调用方应映射为 <c>DevicesPrimaryIdentifierMissing</c> 并拒绝注册。
    /// </summary>
    public static string MachineCode(DeviceIdentity identity)
    {
        string primary = ResolvePrimaryIdentifier(identity);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(MachineCodePrimaryPrefix + primary));
        return Base32Crockford.Encode(hash.AsSpan(0, MachineCodeHashBytes));
    }

    /// <summary>
    /// 按 SMBIOS UUID → 系统盘序列号的顺序选出可用主标识并规范化。
    /// 两者都不可用时抛 <see cref="InvalidOperationException"/>，调用方不得回退到安装 Guid 或 MAC。
    /// </summary>
    public static string ResolvePrimaryIdentifier(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        string primary = NormalizeIdentifier(identity.SmbiosUuid);
        if (!IsUsablePrimary(primary, isSmbios: true))
        {
            primary = NormalizeIdentifier(identity.SystemDiskSerial);
        }

        if (!IsUsablePrimary(primary, isSmbios: false))
        {
            throw new InvalidOperationException(
                "设备身份缺少可用的 SMBIOS UUID 或系统盘序列号，无法生成机器码。");
        }

        return primary;
    }

    /// <summary>
    /// 安装指纹 = SHA-256("{primary}|{installGuid:N}") 小写十六进制。
    /// 只用于临时会话/离线码的设备绑定，不再充当节点或安装实例主键。
    /// </summary>
    public static string InstallationFingerprint(string? primaryIdentifier, Guid installGuid)
    {
        if (installGuid == Guid.Empty)
        {
            throw new ArgumentException("安装指纹缺少安装 Guid。", nameof(installGuid));
        }

        string primary = NormalizeIdentifier(primaryIdentifier);
        return Sha256Hex($"{primary}|{installGuid:N}");
    }

    /// <summary>主键前 <see cref="ShortIdLength"/> 位（日志/人工核对用）。</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..ShortIdLength];

    /// <summary>规范化主标识：只保留字母数字并统一大写（去分隔符、去空白）。</summary>
    public static string NormalizeIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(identifier.Length);
        foreach (char c in identifier)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>规范化 MAC（与主标识同规则），仅供展示/对账使用。</summary>
    public static string NormalizeMac(string? mac) => NormalizeIdentifier(mac);

    private static bool IsUsablePrimary(string normalized, bool isSmbios)
    {
        if (normalized.Length == 0)
        {
            return false;
        }

        if (isSmbios && InvalidSmbiosValues.Contains(normalized))
        {
            return false;
        }

        // 全 0 / 全 F 是主板厂商常见的“未写入”占位值。
        foreach (char c in normalized)
        {
            if (c != '0' && c != 'F')
            {
                return true;
            }
        }

        return false;
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
