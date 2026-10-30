using System.Security.Cryptography;
using Platform.Contracts.Licensing;

namespace Platform.Modules.Licensing.Services;

/// <summary>授权编号生成：<c>LIC-yyyyMMdd-机器码前4位-3 位 Crockford 随机</c>。</summary>
/// <remarks>随机后缀是必须的：同机同日多次签发不会撞号；仍撞号时调用方按重复处理。</remarks>
public static class LicenseNumbers
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>生成自动编号。</summary>
    public static string Generate(DateTimeOffset issuedAt, string machineCode)
    {
        string normalized = Base32Crockford.Normalize(machineCode);
        string suffix = normalized.Length >= 4 ? normalized[..4] : "0000";
        return $"LIC-{issuedAt:yyyyMMdd}-{suffix}-{RandomSuffix()}";
    }

    private static string RandomSuffix()
    {
        byte[] buffer = new byte[3];
        RandomNumberGenerator.Fill(buffer);
        return string.Create(3, buffer, static (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[bytes[i] & 0x1F];
            }
        });
    }
}
