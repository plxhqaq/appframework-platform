using System.Security.Cryptography;

namespace Platform.Contracts.Licensing;

/// <summary>
/// ECDSA P-256 签名/验签（在线快照与离线码共用）。
/// <para>
/// 签名统一为 IEEE-P1363 定长拼接（64 字节），而不是 DER：定长便于离线码定长编码，
/// 也避免不同平台 DER 编码差异导致的验签失败。
/// 私钥只从部署配置/环境变量读取（PKCS#8 Base64），绝不出现在数据库与日志里。
/// </para>
/// </summary>
public static class LicenseCryptography
{
    /// <summary>P-256 的 P1363 签名长度。</summary>
    public const int SignatureLength = 64;

    /// <summary>用私钥对载荷签名。</summary>
    public static byte[] Sign(byte[] payload, string privateKeyPkcs8Base64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using ECDsa key = CreatePrivateKey(privateKeyPkcs8Base64);
        return key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>用单把公钥验签；公钥非法/签名长度不符一律返回 false（不抛）。</summary>
    public static bool Verify(byte[] payload, ReadOnlySpan<byte> signature, string publicKeySpkiBase64)
    {
        if (payload.Length == 0 || signature.Length != SignatureLength || string.IsNullOrWhiteSpace(publicKeySpkiBase64))
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>任一受信任公钥验签通过即成功。</summary>
    public static bool Verify(byte[] payload, ReadOnlySpan<byte> signature, IEnumerable<string> publicKeys)
    {
        foreach (string key in publicKeys)
        {
            if (Verify(payload, signature, key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>构造私钥实例（PKCS#8 Base64）。配置缺失或格式非法时抛 <see cref="ArgumentException"/>。</summary>
    public static ECDsa CreatePrivateKey(string privateKeyPkcs8Base64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
        {
            throw new ArgumentException("签发私钥为空（Licensing:SigningKeyPkcs8 未配置）", nameof(privateKeyPkcs8Base64));
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = Convert.FromBase64String(privateKeyPkcs8Base64.Trim());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("签发私钥不是合法的 Base64", nameof(privateKeyPkcs8Base64), exception);
        }

        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>导出公钥（SPKI Base64）。</summary>
    public static string ExportPublicKeySpkiBase64(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>公钥指纹：SPKI 的 SHA-256（小写十六进制，64 字符）。</summary>
    public static string ComputeFingerprint(string publicKeySpkiBase64)
    {
        byte[] spki = Convert.FromBase64String(publicKeySpkiBase64);
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    /// <summary>由公钥派生密钥 id（<c>lk-</c> + 指纹前 16 位）。</summary>
    public static string ComputeKeyId(string publicKeySpkiBase64) =>
        "lk-" + ComputeFingerprint(publicKeySpkiBase64)[..16];

    /// <summary>从配置私钥导出公钥与密钥 id；未配置或非法时返回 null（调用方按 Unmanaged 处理）。</summary>
    public static string? TryExportPublicKey(string? privateKeyPkcs8Base64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
        {
            return null;
        }

        try
        {
            using ECDsa key = CreatePrivateKey(privateKeyPkcs8Base64);
            return ExportPublicKeySpkiBase64(key);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return null;
        }
    }
}
