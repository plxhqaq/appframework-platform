using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Contracts.Apps;

namespace Platform.Contracts.Licensing;

/// <summary>
/// 离线授权码编解码（无联网现场使用）。
/// <para>
/// 格式 <c>AF4</c>：<c>前缀(3) | payload 字符数(2) | payload | 签名(103) | 校验位(1)</c>，
/// payload = <c>ver(1)=4 | kind(1) | grace(1) | flags(1) | issued(4) | notAfter(4) |
/// machine(10) | appKeyLen(1) | appKey | productKeyLen(1) | productKey | noLen(1) | licenseNo</c>。
/// 时间用 32 位 Unix 秒（1970..2106），签名为 ECDSA P-256 的
/// IEEE-P1363 定长 64 字节；末尾 1 位 Crockford 校验位用于先挡掉手抄/截断错误，
/// 真正的信任仍由验签决定。
/// </para>
/// <para>
/// 与 AF3 的差异：AF4 把 <b>应用键</b>也写进码里——同一机器在不同应用的离线码不能混用；
/// 只接受 AF4，AF3 一律按"版本不支持"拒绝。
/// </para>
/// </summary>
public static partial class OfflineLicenseCodec
{
    /// <summary>当前签发前缀。</summary>
    public const string Prefix = "AF4";

    /// <summary>payload 的格式版本号。</summary>
    public const byte FormatVersion = 4;

    /// <summary>payload 中机器码字节数。</summary>
    public const int MachineCodeBytes = LicenseConventions.MachineCodeBytes;

    /// <summary>
    /// payload 固定段长度：ver(1)+kind(1)+grace(1)+flags(1)+issued(4)+notAfter(4)+machine(10) = 22。
    /// 机器码之后紧跟应用键长度与内容。
    /// </summary>
    public const int PayloadFixedLength = 22;

    /// <summary>签名编码后的字符数（64 字节 = 512 bit → 103 字符）。</summary>
    public const int SignatureChars = 103;

    /// <summary>长度段字符数（payload 字符数按 1 字节编码成 2 字符）。</summary>
    public const int LengthFieldChars = 2;

    /// <summary>校验位字符数。</summary>
    public const int CheckDigitChars = 1;

    [GeneratedRegex(LicenseConventions.ProductKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProductKeyRegex();

    [GeneratedRegex(AppConventions.AppKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyRegex();

    /// <summary>签发离线授权码。</summary>
    public static string Encode(
        string appKey,
        string productKey,
        string machineCode,
        string kind,
        DateTimeOffset issuedUtc,
        DateTimeOffset notAfter,
        int graceDays,
        string privateKeyPkcs8Base64,
        string? licenseNo = null)
    {
        byte[] payload = BuildPayload(
            appKey, productKey, machineCode, kind, issuedUtc, notAfter, graceDays, licenseNo);
        byte[] signature = LicenseCryptography.Sign(payload, privateKeyPkcs8Base64);
        string payloadText = Base32Crockford.Encode(payload);
        if (payloadText.Length > byte.MaxValue)
        {
            throw new ArgumentException(
                $"payload 编码后为 {payloadText.Length} 字符，超出长度段 1 字节上限；请缩短应用键/产品键/编号。");
        }

        string signatureText = Base32Crockford.Encode(signature);
        return Prefix
            + Base32Crockford.Encode([(byte)payloadText.Length])
            + payloadText
            + signatureText
            + ComputeCheckDigit(payload, signature);
    }

    /// <summary>构造 payload（签发端与测试用；客户端只解析）。</summary>
    public static byte[] BuildPayload(
        string appKey,
        string productKey,
        string machineCode,
        string kind,
        DateTimeOffset issuedUtc,
        DateTimeOffset notAfter,
        int graceDays,
        string? licenseNo = null)
    {
        if (!AppKeyRegex().IsMatch(appKey ?? string.Empty))
        {
            throw new ArgumentException($"应用键必须匹配 {AppConventions.AppKeyPattern}", nameof(appKey));
        }

        if (!ProductKeyRegex().IsMatch(productKey ?? string.Empty))
        {
            throw new ArgumentException($"产品键必须匹配 {LicenseConventions.ProductKeyPattern}", nameof(productKey));
        }

        string key = productKey!;

        if (!LicenseConventions.TryNormalizeMachineCode(machineCode, out string normalizedMachine))
        {
            throw new ArgumentException(
                $"机器码不合法（应为 {LicenseConventions.MachineCodeChars} 字符 Crockford Base32）",
                nameof(machineCode));
        }

        if (!LicenseConventions.IsValidKind(kind))
        {
            throw new ArgumentException($"未知授权类型：{kind}", nameof(kind));
        }

        if (graceDays is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(graceDays), graceDays, "宽限天数必须在 0..255 之间");
        }

        long issued = issuedUtc.ToUnixTimeSeconds();
        long after = notAfter.ToUnixTimeSeconds();
        if (issued is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(issuedUtc), "签发时间超出可编码范围（1970..2106）");
        }

        if (after is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(notAfter), "到期时间超出可编码范围（1970..2106）");
        }

        if (after <= issued)
        {
            throw new ArgumentException("到期时间必须晚于签发时间", nameof(notAfter));
        }

        if (!Base32Crockford.TryDecode(normalizedMachine, out byte[] machine) || machine.Length != MachineCodeBytes)
        {
            throw new ArgumentException("机器码不合法（应为 10 字节哈希）", nameof(machineCode));
        }

        byte[] app = Encoding.ASCII.GetBytes(appKey!.Trim());
        byte[] product = Encoding.ASCII.GetBytes(key);
        byte[] no = EncodeLicenseNo(licenseNo);

        var payload = new byte[PayloadFixedLength + 1 + app.Length + 1 + product.Length + 1 + no.Length];
        payload[0] = FormatVersion;
        payload[1] = kind == LicenseConventions.KindTrial ? (byte)0 : (byte)1;
        payload[2] = (byte)graceDays;
        payload[3] = 0; // flags：保留
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)issued);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), (uint)after);
        machine.CopyTo(payload, 12);
        payload[PayloadFixedLength] = (byte)app.Length;
        app.CopyTo(payload, PayloadFixedLength + 1);
        int productOffset = PayloadFixedLength + 1 + app.Length;
        payload[productOffset] = (byte)product.Length;
        product.CopyTo(payload, productOffset + 1);
        int noOffset = productOffset + 1 + product.Length;
        payload[noOffset] = (byte)no.Length;
        no.CopyTo(payload, noOffset + 1);
        return payload;
    }

    /// <summary>
    /// 解析并验签。只判定"码本身是否合法"（前缀/长度/校验位/版本/签名）；
    /// 是否过期、是否本机、产品是否匹配由调用方判定。
    /// </summary>
    public static bool TryParse(
        string? code,
        IReadOnlyCollection<string> trustedPublicKeys,
        out OfflineLicenseCodeContent content,
        out string? error)
    {
        content = null!;
        error = null;

        string normalized = Base32Crockford.Normalize(code);
        if (normalized.Length == 0)
        {
            error = "授权码为空";
            return false;
        }

        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = normalized.StartsWith("AF3", StringComparison.Ordinal)
                ? "授权码版本不支持（AF3），请使用 AF4 离线码。"
                : $"授权码前缀不是 {Prefix}（请确认复制完整）";
            return false;
        }

        string body = normalized[Prefix.Length..];
        if (body.Length < LengthFieldChars + SignatureChars + CheckDigitChars)
        {
            error = $"授权码长度不正确（{Prefix} 至少 {Prefix.Length + LengthFieldChars + SignatureChars + CheckDigitChars} 字符，实际 {normalized.Length} 字符）";
            return false;
        }

        if (!Base32Crockford.TryDecode(body[..LengthFieldChars], out byte[] lengthBytes) || lengthBytes.Length != 1)
        {
            error = "授权码内容无法解析（长度段损坏）";
            return false;
        }

        int payloadChars = lengthBytes[0];
        int expected = Prefix.Length + LengthFieldChars + payloadChars + SignatureChars + CheckDigitChars;
        if (payloadChars == 0 || normalized.Length != expected)
        {
            error = $"授权码长度不正确（应为 {expected} 字符，实际 {normalized.Length} 字符）";
            return false;
        }

        string payloadText = body.Substring(LengthFieldChars, payloadChars);
        string signatureText = body.Substring(LengthFieldChars + payloadChars, SignatureChars);
        string checkText = body.Substring(LengthFieldChars + payloadChars + SignatureChars, CheckDigitChars);

        if (!Base32Crockford.TryDecode(payloadText, out byte[] payload))
        {
            error = "授权码内容无法解析（payload 段损坏）";
            return false;
        }

        if (!Base32Crockford.TryDecode(signatureText, out byte[] signature)
            || signature.Length != LicenseCryptography.SignatureLength)
        {
            error = "授权码内容无法解析（签名段损坏）";
            return false;
        }

        if (!string.Equals(ComputeCheckDigit(payload, signature), NormalizeCheckDigit(checkText), StringComparison.Ordinal))
        {
            error = "授权码校验位不匹配（码被截断或抄错）";
            return false;
        }

        if (!TryReadPayload(payload, out content, out error))
        {
            return false;
        }

        if (trustedPublicKeys.Count == 0)
        {
            error = "没有可用的受信任公钥，无法校验授权码";
            return false;
        }

        if (!LicenseCryptography.Verify(payload, signature, trustedPublicKeys))
        {
            error = "授权码验签失败（码被篡改或不是本系统签发）";
            return false;
        }

        content = content with { RawCode = normalized };
        return true;
    }

    /// <summary>
    /// 校验位按解码器的容错口径归一：I/L 视作 1、O 视作 0。
    /// 手抄时常把 0 写成 O，不应因此把合法码判成"抄错"。
    /// </summary>
    private static string NormalizeCheckDigit(string value) =>
        value.Replace('I', '1').Replace('L', '1').Replace('O', '0');

    /// <summary>校验位：payload+signature 的 SHA-256 首字节高 5 位，映射到 Crockford 字符。</summary>
    private static string ComputeCheckDigit(byte[] payload, byte[] signature)
    {
        byte[] combined = new byte[payload.Length + signature.Length];
        payload.CopyTo(combined, 0);
        signature.CopyTo(combined, payload.Length);
        return Base32Crockford.Encode(SHA256.HashData(combined).AsSpan(0, 1))[..1];
    }

    private static bool TryReadPayload(byte[] payload, out OfflineLicenseCodeContent content, out string? error)
    {
        content = null!;
        error = null;

        if (payload.Length < PayloadFixedLength + 3)
        {
            error = "授权码内容无法解析（payload 过短）";
            return false;
        }

        if (payload[0] != FormatVersion)
        {
            error = $"授权码版本不支持（{payload[0]}，当前支持 {FormatVersion}）";
            return false;
        }

        string kind = payload[1] switch
        {
            0 => LicenseConventions.KindTrial,
            1 => LicenseConventions.KindOfficial,
            _ => string.Empty,
        };
        if (kind.Length == 0)
        {
            error = "授权码类型非法";
            return false;
        }

        int graceDays = payload[2];
        DateTimeOffset issuedUtc = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4)));
        DateTimeOffset notAfter = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)));
        string machineCode = Base32Crockford.Encode(payload.AsSpan(12, MachineCodeBytes));

        int appLength = payload[PayloadFixedLength];
        int appOffset = PayloadFixedLength + 1;
        if (appLength is 0 or > 64 || appOffset + appLength >= payload.Length)
        {
            error = "授权码内容无法解析（应用键长度非法）";
            return false;
        }

        string appKey = Encoding.ASCII.GetString(payload, appOffset, appLength);
        if (!AppKeyRegex().IsMatch(appKey))
        {
            error = "授权码内容无法解析（应用键非法）";
            return false;
        }

        int productOffset = appOffset + appLength;
        int productLength = payload[productOffset];
        productOffset++;
        if (productLength is 0 or > LicenseConventions.MaxProductKeyChars)
        {
            error = "授权码内容无法解析（产品键长度非法）";
            return false;
        }

        int noOffset = productOffset + productLength;
        if (noOffset >= payload.Length)
        {
            error = "授权码内容无法解析（编号段缺失）";
            return false;
        }

        int noLength = payload[noOffset];
        if (noLength > LicenseConventions.MaxLicenseNoChars || noOffset + 1 + noLength != payload.Length)
        {
            error = "授权码内容无法解析（编号段长度不一致）";
            return false;
        }

        string productKey = Encoding.ASCII.GetString(payload, productOffset, productLength);
        if (!ProductKeyRegex().IsMatch(productKey))
        {
            error = "授权码内容无法解析（产品键非法）";
            return false;
        }

        string? licenseNo = noLength == 0
            ? null
            : Encoding.ASCII.GetString(payload, noOffset + 1, noLength);

        content = new OfflineLicenseCodeContent(
            productKey,
            machineCode,
            appKey,
            kind,
            issuedUtc,
            notAfter,
            graceDays,
            licenseNo,
            RawCode: string.Empty);
        return true;
    }

    private static byte[] EncodeLicenseNo(string? licenseNo)
    {
        if (string.IsNullOrWhiteSpace(licenseNo))
        {
            return [];
        }

        string trimmed = licenseNo.Trim();
        if (trimmed.Length > LicenseConventions.MaxLicenseNoChars)
        {
            throw new ArgumentException(
                $"授权编号不能超过 {LicenseConventions.MaxLicenseNoChars} 个字符", nameof(licenseNo));
        }

        foreach (char c in trimmed)
        {
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException("授权编号只能包含可打印 ASCII 字符（不支持中文/空格）", nameof(licenseNo));
            }
        }

        return Encoding.ASCII.GetBytes(trimmed);
    }
}

/// <summary>离线码解析结果（未做时间、机器码、产品与应用键判定）。</summary>
public sealed record OfflineLicenseCodeContent(
    string ProductKey,
    string MachineCode,
    string AppKey,
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter,
    int GraceDays,
    string? LicenseNo,
    string RawCode);
