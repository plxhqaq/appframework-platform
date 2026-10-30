using System.Buffers.Binary;
using System.Text;

namespace Platform.Contracts.Licensing;

/// <summary>
/// 在线授权快照（带签名的授权事实）。
/// <para>
/// 格式：<c>magic("AFLIC2\0") | ver | kind | status | reserved | grace(4) |
/// notBefore(8) | notAfter(8) | serverTime(8) | machine(10) |
/// appKey | productKey | licenseNo | keyId | deviceCode | signature(64)</c>，
/// 字符串为 1 字节长度 + UTF-8。整段 Base64 后即为 <see cref="LicenseStatusResponse.Snapshot"/>。
/// 签名为 ECDSA P-256 over 载荷（不含签名自身）的 IEEE-P1363 定长 64 字节。
/// </para>
/// <para>字段顺序一经发布不得改动；新增字段只能追加到签名之前并升 <see cref="FormatVersion"/>。</para>
/// </summary>
public static class LicenseSnapshotCodec
{
    /// <summary>快照格式版本。</summary>
    public const int FormatVersion = 2;

    private static ReadOnlySpan<byte> Magic => "AFLIC2\0"u8;

    /// <summary>签名并返回 Base64 快照。</summary>
    public static string Sign(LicenseSnapshotPayload payload, string privateKeyPkcs8Base64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] body = BuildPayload(payload);
        byte[] signature = LicenseCryptography.Sign(body, privateKeyPkcs8Base64);
        byte[] token = new byte[body.Length + signature.Length];
        body.CopyTo(token, 0);
        signature.CopyTo(token, body.Length);
        return Convert.ToBase64String(token);
    }

    /// <summary>构造确定性载荷（不含签名）。</summary>
    public static byte[] BuildPayload(LicenseSnapshotPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!LicenseConventions.TryNormalizeMachineCode(payload.MachineCode, out string machineCode))
        {
            throw new ArgumentException("机器码不合法（应为 16 字符 Crockford Base32）", nameof(payload));
        }

        if (!LicenseConventions.IsValidKind(payload.Kind) || !LicenseConventions.IsValidStatus(payload.Status))
        {
            throw new ArgumentException("授权类型或状态非法", nameof(payload));
        }

        if (!Base32Crockford.TryDecode(machineCode, out byte[] machine) || machine.Length != LicenseConventions.MachineCodeBytes)
        {
            throw new ArgumentException("机器码不合法（应为 10 字节哈希）", nameof(payload));
        }

        var buffer = new List<byte>(192);
        buffer.AddRange(Magic);
        buffer.Add(FormatVersion);
        buffer.Add(ToKindByte(payload.Kind));
        buffer.Add(ToStatusByte(payload.Status));
        buffer.Add(0); // 保留位：便于追加开关而不破格式

        Span<byte> span4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(span4, payload.GraceDays);
        buffer.AddRange(span4.ToArray());

        WriteInt64(buffer, payload.NotBefore.ToUnixTimeSeconds());
        WriteInt64(buffer, payload.NotAfter.ToUnixTimeSeconds());
        WriteInt64(buffer, payload.ServerTime.ToUnixTimeSeconds());
        buffer.AddRange(machine);
        WriteString(buffer, payload.AppKey);
        WriteString(buffer, payload.ProductKey);
        WriteString(buffer, payload.LicenseNo);
        WriteString(buffer, payload.KeyId);
        WriteString(buffer, payload.DeviceCode);
        return [.. buffer];
    }

    /// <summary>
    /// 解析并验签。任一受信任公钥验签通过即成功；公钥集为空、签名不符或格式损坏返回 false。
    /// </summary>
    public static bool TryVerify(
        string? token,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out LicenseSnapshotPayload payload,
        out string? error)
    {
        payload = null!;
        if (!TryParseUnverified(token, out LicenseSnapshotPayload unverified, out byte[] signature, out error))
        {
            return false;
        }

        if (trustedPublicKeys.Count == 0)
        {
            error = "没有可用的受信任公钥";
            return false;
        }

        if (unverified.KeyId is { Length: > 0 } keyId
            && trustedPublicKeys.TryGetValue(keyId, out string? pinned)
            && LicenseCryptography.Verify(
                BuildPayload(unverified), signature, pinned))
        {
            payload = unverified;
            return true;
        }

        if (LicenseCryptography.Verify(BuildPayload(unverified), signature, trustedPublicKeys.Values))
        {
            payload = unverified;
            return true;
        }

        error = "快照验签失败（内容被篡改或不是本系统签发）";
        return false;
    }

    /// <summary>只解析结构、不验签（仅供校验函数与测试使用）。</summary>
    public static bool TryParseUnverified(
        string? token,
        out LicenseSnapshotPayload payload,
        out byte[] signature,
        out string? error)
    {
        payload = null!;
        signature = [];
        error = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "快照为空";
            return false;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(token.Trim());
        }
        catch (FormatException)
        {
            error = "快照不是合法的 Base64";
            return false;
        }

        int signatureOffset = raw.Length - LicenseCryptography.SignatureLength;
        if (signatureOffset <= Magic.Length)
        {
            error = "快照长度不足";
            return false;
        }

        ReadOnlySpan<byte> body = raw.AsSpan(0, signatureOffset);
        if (!body.StartsWith(Magic))
        {
            error = "快照魔数不符";
            return false;
        }

        signature = raw[signatureOffset..];
        int index = Magic.Length;
        try
        {
            byte version = body[index++];
            if (version != FormatVersion)
            {
                error = $"快照版本不支持（{version}，当前支持 {FormatVersion}）";
                return false;
            }

            string kind = FromKindByte(body[index++]);
            string status = FromStatusByte(body[index++]);
            index++; // 保留位

            int graceDays = BinaryPrimitives.ReadInt32BigEndian(body.Slice(index, 4));
            index += 4;

            DateTimeOffset notBefore = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(body.Slice(index, 8)));
            index += 8;
            DateTimeOffset notAfter = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(body.Slice(index, 8)));
            index += 8;
            DateTimeOffset serverTime = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(body.Slice(index, 8)));
            index += 8;

            string machineCode = Base32Crockford.Encode(body.Slice(index, LicenseConventions.MachineCodeBytes));
            index += LicenseConventions.MachineCodeBytes;

            string appKey = ReadString(body, ref index);
            string productKey = ReadString(body, ref index);
            string licenseNo = ReadString(body, ref index);
            string keyId = ReadString(body, ref index);
            string deviceCode = ReadString(body, ref index);

            payload = new LicenseSnapshotPayload(
                appKey,
                productKey,
                machineCode,
                string.IsNullOrEmpty(deviceCode) ? null : deviceCode,
                kind,
                status,
                notBefore,
                notAfter,
                graceDays,
                serverTime,
                string.IsNullOrEmpty(licenseNo) ? null : licenseNo,
                keyId);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "快照内容被截断";
            return false;
        }
        catch (ArgumentException)
        {
            error = "快照内容无法解析";
            return false;
        }
    }

    /// <summary>授权类型转字节（0 试用，1 正式）。</summary>
    public static byte ToKindByte(string kind) => kind switch
    {
        LicenseConventions.KindTrial => 0,
        LicenseConventions.KindOfficial => 1,
        _ => throw new ArgumentException($"未知授权类型：{kind}", nameof(kind)),
    };

    /// <summary>授权状态转字节。</summary>
    public static byte ToStatusByte(string status) => status switch
    {
        LicenseConventions.StatusUnlicensed => 0,
        LicenseConventions.StatusActive => 1,
        LicenseConventions.StatusExpired => 2,
        LicenseConventions.StatusRevoked => 3,
        LicenseConventions.StatusUnmanaged => 4,
        _ => throw new ArgumentException($"未知授权状态：{status}", nameof(status)),
    };

    private static string FromKindByte(byte value) => value switch
    {
        0 => LicenseConventions.KindTrial,
        1 => LicenseConventions.KindOfficial,
        _ => throw new ArgumentException($"未知授权类型字节：{value}"),
    };

    private static string FromStatusByte(byte value) => value switch
    {
        0 => LicenseConventions.StatusUnlicensed,
        1 => LicenseConventions.StatusActive,
        2 => LicenseConventions.StatusExpired,
        3 => LicenseConventions.StatusRevoked,
        4 => LicenseConventions.StatusUnmanaged,
        _ => throw new ArgumentException($"未知授权状态字节：{value}"),
    };

    private static void WriteString(List<byte> buffer, string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > byte.MaxValue)
        {
            throw new ArgumentException($"字段过长（{bytes.Length} 字节 > 255）", nameof(value));
        }

        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> body, ref int index)
    {
        if (index >= body.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "字符串长度字段越界");
        }

        int length = body[index++];
        if (index + length > body.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "字符串内容越界");
        }

        string text = Encoding.UTF8.GetString(body.Slice(index, length));
        index += length;
        return text;
    }

    private static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> span = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        buffer.AddRange(span.ToArray());
    }
}

/// <summary>快照解析后的内容（已验签时可信）。</summary>
public sealed record LicenseSnapshotPayload(
    string AppKey,
    string ProductKey,
    string MachineCode,
    string? DeviceCode,
    string Kind,
    string Status,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    int GraceDays,
    DateTimeOffset ServerTime,
    string? LicenseNo,
    string KeyId);
