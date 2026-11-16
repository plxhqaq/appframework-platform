using System.Security.Cryptography;
using System.Text;
using Platform.Contracts.Licensing;

namespace Platform.Contracts.Devices;

/// <summary>
/// 设备稳定键与机器码算法。纯函数，客户端与服务端必须得到完全一致的结果。
/// </summary>
/// <remarks>
/// <para>
/// 稳定键 <c>DeviceKey = SHA-256("{PrimaryIdentifier}|{InstallGuid:N}")</c>（小写十六进制）：
/// 只由机器级主标识与安装 Guid 决定，版本号不参与，升级不会产生幽灵设备。
/// 主标识缺失时退化为仅安装 Guid——此时同一台机器重装会变成新设备，管理端应提示。
/// </para>
/// <para>
/// 机器码 = SHA-256(seed) 前 10 字节的 Crockford Base32（16 字符），seed 优先用主标识，
/// 缺失时回退稳定键。<b>机器码不含 InstallGuid 时的稳定性来自主标识</b>；主标识为空时它随安装变化，
/// 只用于展示/排障，不能当作永久授权锚点。
/// </para>
/// <para>
/// MAC 不参与以上任何计算：老客户端的新版指纹同样不使用 MAC，MAC 只作辅助展示。
/// </para>
/// </remarks>
public static class DeviceIdentityHasher
{
    /// <summary>短 ID 长度（稳定键前 16 位十六进制字符）。</summary>
    public const int ShortIdLength = 16;

    /// <summary>机器码字符长度（Crockford Base32，10 字节哈希 = 16 字符）。</summary>
    public const int MachineCodeLength = 16;

    /// <summary>参与机器码哈希的字节数（80 bit，碰撞概率可忽略）。</summary>
    private const int MachineCodeHashBytes = 10;

    private const string MachineCodePrimaryPrefix = "AF-MACHINE|";

    private const string MachineCodeFallbackPrefix = "AF-MACHINE|FP|";

    /// <summary>
    /// 计算稳定键与机器码。安装 Guid 为空表示客户端身份不可用，直接拒绝。
    /// </summary>
    /// <exception cref="ArgumentException">安装 Guid 为空。</exception>
    public static (string DeviceKey, string MachineCode) Compute(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.InstallGuid == Guid.Empty)
        {
            throw new ArgumentException("设备身份缺少安装 Guid，无法生成稳定键。", nameof(identity));
        }

        string primary = NormalizeIdentifier(identity.PrimaryIdentifier);
        string deviceKey = Sha256Hex($"{primary}|{identity.InstallGuid:N}");
        return (deviceKey, ComputeMachineCode(primary, deviceKey));
    }

    /// <summary>
    /// 机器码：主标识优先；为空时回退给定指纹（通常传稳定键）。
    /// 两者都为空时返回空串，调用方不得据此发码。
    /// </summary>
    public static string ComputeMachineCode(string? primaryIdentifier, string? fallbackFingerprint)
    {
        string normalized = NormalizeIdentifier(primaryIdentifier);
        if (normalized.Length == 0 && string.IsNullOrWhiteSpace(fallbackFingerprint))
        {
            return string.Empty;
        }

        string seed = normalized.Length > 0
            ? MachineCodePrimaryPrefix + normalized
            : MachineCodeFallbackPrefix + fallbackFingerprint!.Trim().ToLowerInvariant();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Base32Crockford.Encode(hash.AsSpan(0, MachineCodeHashBytes));
    }

    /// <summary>稳定键前 <see cref="ShortIdLength"/> 位（日志/人工核对用）。</summary>
    public static string ShortId(string deviceKey)
    {
        ArgumentNullException.ThrowIfNull(deviceKey);
        return deviceKey.Length <= ShortIdLength ? deviceKey : deviceKey[..ShortIdLength];
    }

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

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
